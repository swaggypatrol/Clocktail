using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Clocktail.Hw;

/// <summary>
/// The one place that invokes a driver function pointer.
/// <para>
/// Every call goes through the same gate, in this order: the slot must be callable, the buffer must have the
/// slot's size and version word, a write must pass its <see cref="WritePolicy"/>, the interface must exist,
/// the session must not be faulted. Only then is the driver called, with the structure surrounded by guard
/// bytes (0xCD, 0x40 before and 0x1000 after) that are verified afterwards. A reply is
/// copied back only on status 0; a handle-loss status poisons the session.
/// </para>
/// </summary>
public sealed unsafe class NvClient
{
    private const byte Guard = 0xCD;
    private const int Head = 0x40;
    private const int Tail = 0x1000;
    private const byte Sentinel = 0xA5;
    private const int SentinelTail = 0x1000;

    public NvClient(INvApi api, WritePolicySet? policies = null)
    {
        Api = api;
        Policies = policies ?? WritePolicySet.Default;
    }

    public INvApi Api { get; }
    public WritePolicySet Policies { get; }

    /// <summary>A zeroed buffer of the slot's size with its version word filled in.</summary>
    public static byte[] NewBuffer(Slot slot)
    {
        var info = Slots.Info(slot);
        var shape = info.Shape ?? throw new InvalidOperationException($"{slot}: structure size is unknown; probe it first.");
        var b = new byte[info.Buffer ?? shape.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, shape.Word);
        return b;
    }

    /// <summary>Same as <see cref="NewBuffer"/> for a struct type whose size must equal the slot's size.</summary>
    public static T NewStruct<T>(Slot slot) where T : unmanaged
    {
        var shape = Slots.Info(slot).Shape ?? throw new InvalidOperationException($"{slot}: structure size is unknown; probe it first.");
        if (sizeof(T) != shape.Size) throw new InvalidOperationException($"{typeof(T).Name} is 0x{sizeof(T):x} bytes but {slot} is {shape}.");
        T v = default;
        BinaryPrimitives.WriteUInt32LittleEndian(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref v, 1)), shape.Word);
        return v;
    }

    /// <summary><c>fn(gpu, buf)</c> for <see cref="Signature.GpuStruct"/> slots.</summary>
    public NvResult Call(GpuSession gpu, Slot slot, Span<byte> buf) => CallStruct(gpu, slot, null, buf);

    /// <summary><c>fn(gpu, index, buf)</c> for <see cref="Signature.GpuIndexStruct"/> slots.</summary>
    public NvResult Call(GpuSession gpu, Slot slot, uint index, Span<byte> buf) => CallStruct(gpu, slot, index, buf);

    public NvResult Call<T>(GpuSession gpu, Slot slot, ref T value) where T : unmanaged =>
        CallStruct(gpu, slot, null, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));

    public NvResult Call<T>(GpuSession gpu, Slot slot, uint index, ref T value) where T : unmanaged =>
        CallStruct(gpu, slot, index, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));

    /// <summary>
    /// A read with a caller-chosen size and version word, bypassing the shape table. Only for readable
    /// plain-structure slots, and only for diagnostics (size probing). Guards and the fault latch still apply.
    /// </summary>
    public NvResult CallRaw(GpuSession gpu, Slot slot, Span<byte> buf)
    {
        var info = Slots.Info(slot);
        if (info.Access != SlotAccess.Read) return NvResult.Rejected(slot, "raw calls are read-only");
        if (info.Signature != Signature.GpuStruct) return NvResult.Rejected(slot, "raw calls need a plain structure interface");
        if (buf.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(buf) == 0) return NvResult.Rejected(slot, "version word not set");
        nint fn = Api.Resolve(slot);
        if (fn == 0) return NvResult.Missing(slot);
        return Invoke(gpu, slot, fn, null, buf, Guard, Head, Tail);
    }

    /// <summary><c>fn(gpu, uint*)</c> for <see cref="Signature.GpuScalar"/> slots.</summary>
    public NvResult ReadScalar(GpuSession gpu, Slot slot, out uint value)
    {
        value = 0;
        var info = Slots.Info(slot);
        if (info.Signature != Signature.GpuScalar) return NvResult.Rejected(slot, "not a scalar read");
        nint fn = Api.Resolve(slot);
        if (fn == 0) return NvResult.Missing(slot);
        if (gpu.Faulted) return NvResult.Poisoned(slot, gpu.FaultStatus);
        uint v = 0;
        int rc = ((delegate* unmanaged[Cdecl]<nint, uint*, int>)fn)(gpu.Handle, &v);
        if (NvStatus.IsHandleLoss(rc)) gpu.Poison(rc);
        if (rc == 0) value = v;
        return NvResult.FromStatus(slot, rc);
    }

    /// <summary>
    /// The 0xA5-sentinel single-domain read (<see cref="Signature.GpuDomainSentinel"/>): a 12-byte
    /// {version, domain, out} record in a 0x100c buffer filled with 0xA5; the tail must survive and the
    /// output must not be -1.
    /// </summary>
    public NvResult ReadDomain(GpuSession gpu, Slot slot, uint domain, out uint value)
    {
        value = 0;
        var info = Slots.Info(slot);
        if (info.Signature != Signature.GpuDomainSentinel) return NvResult.Rejected(slot, "not a sentinel domain read");
        var shape = info.Shape!.Value;
        nint fn = Api.Resolve(slot);
        if (fn == 0) return NvResult.Missing(slot);

        Span<byte> rec = stackalloc byte[shape.Size];
        rec.Fill(Sentinel);
        BinaryPrimitives.WriteUInt32LittleEndian(rec, shape.Word);
        BinaryPrimitives.WriteUInt32LittleEndian(rec[4..], domain);
        var r = Invoke(gpu, slot, fn, null, rec, Sentinel, 0, SentinelTail);
        if (!r.IsOk) return r;
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(rec[8..]);
        if (v == 0xffffffff) return new NvResult(NvOutcome.Nvapi, NvStatus.Error, slot, $"driver returned -1 for domain 0x{domain:x}");
        value = v;
        return r;
    }

    /// <summary>Which slots this driver exposes. Resolving is harmless; forbidden slots are not even looked up.</summary>
    public IReadOnlyDictionary<Slot, bool> Presence()
    {
        var d = new Dictionary<Slot, bool>();
        foreach (var s in Slots.All)
            if (s.Access != SlotAccess.Forbidden) d[s.Slot] = Api.Resolve(s.Slot) != 0;
        return d;
    }

    private NvResult CallStruct(GpuSession gpu, Slot slot, uint? index, Span<byte> buf)
    {
        var info = Slots.Info(slot);
        if (info.Access == SlotAccess.Forbidden) return NvResult.Rejected(slot, "forbidden by design");
        switch (info.Signature)
        {
            case Signature.GpuStruct when index != null: return NvResult.Rejected(slot, "takes no index");
            case Signature.GpuIndexStruct when index == null: return NvResult.Rejected(slot, "needs an index");
            case Signature.GpuScalar: return NvResult.Rejected(slot, "scalar interface: use ReadScalar");
            case Signature.GpuDomainSentinel: return NvResult.Rejected(slot, "sentinel interface: use ReadDomain");
        }
        if (buf.Length < 4) return NvResult.Rejected(slot, "buffer too small for a version word");
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        if (info.Shape is { } shape)
        {
            if (buf.Length != (info.Buffer ?? shape.Size)) return NvResult.Rejected(slot, $"buffer is 0x{buf.Length:x} bytes, structure is {shape}" + (info.Buffer is { } want ? $" in a 0x{want:x} buffer" : ""));
            if (word != shape.Word) return NvResult.Rejected(slot, $"version word 0x{word:x} does not match {shape}");
        }
        else if (word == 0)
        {
            return NvResult.Rejected(slot, "version word not set (structure size unknown; caller must supply it)");
        }
        if (info.IsWrite)
        {
            var why = Policies.Check(slot, buf);
            if (why != null) return NvResult.Rejected(slot, why);
        }
        nint fn = Api.Resolve(slot);
        if (fn == 0) return NvResult.Missing(slot);
        return Invoke(gpu, slot, fn, index, buf, Guard, Head, Tail);
    }

    private static NvResult Invoke(GpuSession gpu, Slot slot, nint fn, uint? index, Span<byte> buf, byte fill, int head, int tail)
    {
        if (gpu.Faulted) return NvResult.Poisoned(slot, gpu.FaultStatus);
        int total = head + buf.Length + tail;
        byte* mem = (byte*)NativeMemory.Alloc((nuint)total);
        try
        {
            new Span<byte>(mem, total).Fill(fill);
            buf.CopyTo(new Span<byte>(mem + head, buf.Length));

            int rc = index is { } i
                ? ((delegate* unmanaged[Cdecl]<nint, uint, byte*, int>)fn)(gpu.Handle, i, mem + head)
                : ((delegate* unmanaged[Cdecl]<nint, byte*, int>)fn)(gpu.Handle, mem + head);

            if (NvStatus.IsHandleLoss(rc)) gpu.Poison(rc);

            int bad = new ReadOnlySpan<byte>(mem, head).IndexOfAnyExcept(fill);
            if (bad >= 0) return NvResult.GuardBroken(slot, $"{head - bad} bytes before the structure");
            bad = new ReadOnlySpan<byte>(mem + head + buf.Length, tail).IndexOfAnyExcept(fill);
            if (bad >= 0) return NvResult.GuardBroken(slot, $"{bad + 1} bytes past the structure");

            if (rc == 0) new ReadOnlySpan<byte>(mem + head, buf.Length).CopyTo(buf);
            return NvResult.FromStatus(slot, rc);
        }
        finally
        {
            NativeMemory.Free(mem);
        }
    }
}
