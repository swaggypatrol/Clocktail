using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Clocktail.Domain.Broker;

namespace Clocktail.Tests.Domain;

public class BrokerClientBoundaryTests
{
    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(4294967295)]
    public void Invalid_wait_is_rejected_before_connecting_or_sending(double milliseconds)
    {
        string name = "clocktail-invalid-wait-" + Guid.NewGuid().ToString("n");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancel = new CancellationTokenSource();
        var accepted = pipe.WaitForConnectionAsync(cancel.Token);
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BrokerClient.TryCall(new BrokerRequest("must-not-send"), name, answerWait: TimeSpan.FromMilliseconds(milliseconds)));
            Assert.False(accepted.IsCompletedSuccessfully);
        }
        finally { cancel.Cancel(); }
    }

    [Fact]
    public void Malformed_response_is_a_transport_error_not_a_leaked_json_exception()
    {
        using var stream = new MemoryStream();
        byte[] data = Encoding.UTF8.GetBytes("{not json");
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        stream.Write(length);
        stream.Write(data);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => BrokerTransport.ReadResponse(stream));
    }
}
