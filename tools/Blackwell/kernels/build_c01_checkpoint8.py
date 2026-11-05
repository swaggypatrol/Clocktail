"""Build only the opt-in C01 checkpoint8 resource with the project's pinned NVRTC.

  python tools/Blackwell/kernels/build_c01_checkpoint8.py --dependency-root PATH_TO_BLACKWELL_TUNER

Uses the existing deps/pip NVRTC toolchain; no CUDA context, GPU workload, download or original-image rewrite.
The separate image basename prevents an old default cubin from hiding a newly compiled checkpoint8 PTX.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path


OPTIONS = ["-std=c++17", "-default-device", "--fmad=true", "-ftz=false",
           "-prec-div=true", "-prec-sqrt=true", "--ptxas-options=-v", "-DC01_CHECKPOINT8_ONLY=1"]


def require(code, operation):
    if code != 0:
        raise RuntimeError(f"{operation} failed: NVRTC {code}")


def compile_image(rt, source, source_name, options, kind):
    program = ctypes.c_void_p()
    require(rt.nvrtcCreateProgram(ctypes.byref(program), source, source_name.encode(), 0, None, None), "create program")
    try:
        argv = (ctypes.c_char_p * len(options))(*(value.encode() for value in options))
        status = rt.nvrtcCompileProgram(program, len(options), argv)
        size = ctypes.c_size_t()
        require(rt.nvrtcGetProgramLogSize(program, ctypes.byref(size)), "log size")
        log = ctypes.create_string_buffer(max(1, size.value))
        require(rt.nvrtcGetProgramLog(program, log), "log")
        message = log.value.decode(errors="replace")
        if status != 0:
            raise RuntimeError(f"checkpoint8 {kind} compilation failed ({status}):\n{message}")
        get_size, get = (rt.nvrtcGetPTXSize, rt.nvrtcGetPTX) if kind == "ptx" else (rt.nvrtcGetCUBINSize, rt.nvrtcGetCUBIN)
        require(get_size(program, ctypes.byref(size)), f"{kind} size")
        image = ctypes.create_string_buffer(size.value)
        require(get(program, image), kind)
        data = image.raw.rstrip(b"\0") if kind == "ptx" else image.raw
        return data, message
    finally:
        require(rt.nvrtcDestroyProgram(ctypes.byref(program)), "destroy program")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dependency-root", type=Path, required=True)
    args = parser.parse_args()
    dependencies = args.dependency_root.resolve() / "deps" / "pip" / "nvidia" / "cu13"
    binaries = dependencies / "bin" / "x86_64"
    choices = sorted(p for p in binaries.glob("nvrtc64_1*.dll") if "builtins" not in p.name and ".alt." not in p.name)
    if not choices:
        raise FileNotFoundError(f"existing NVRTC dependency missing: {binaries}")
    root = Path(__file__).resolve().parents[1]
    source_path = root / "kernels" / "compute" / "c01_fp32_fma.cu"
    source = source_path.read_bytes()
    output = root / "kernels" / "images" / "compute"
    name = "c01_fp32_fma_checkpoint8"
    with os.add_dll_directory(str(binaries)):
        builtins = [ctypes.CDLL(str(p)) for p in binaries.glob("nvrtc-builtins64_*.dll")]
        rt = ctypes.CDLL(str(choices[-1]))
        major, minor = ctypes.c_int(), ctypes.c_int()
        require(rt.nvrtcVersion(ctypes.byref(major), ctypes.byref(minor)), "version")
        metadata = {"nvrtc_version": f"{major.value}.{minor.value}", "nvrtc_dll": choices[-1].name,
                    "source": "kernels/compute/c01_fp32_fma.cu", "source_sha256": hashlib.sha256(source).hexdigest(),
                    "hash_interval": 8, "renorm_interval": 32, "outputs": {}}
        images = {}
        for kind, architecture, suffix in [("ptx", "compute_120", ".ptx"), ("cubin", "sm_120", ".sm_120.cubin")]:
            options = [f"-arch={architecture}"] + OPTIONS
            image, log = compile_image(rt, source, source_path.name, options, kind)
            filename = name + suffix
            images[filename] = image
            metadata["outputs"][filename] = {"sha256": hashlib.sha256(image).hexdigest(), "options": options, "compiler_log": log}
        # Compile both successfully before replacing either output; default c01_fp32_fma images are never touched.
        output.mkdir(parents=True, exist_ok=True)
        for filename, image in images.items():
            (output / filename).write_bytes(image)
        (output / (name + ".build.json")).write_text(json.dumps(metadata, indent=2), encoding="utf-8")
        print(f"{name}: NVRTC {major.value}.{minor.value}; " + ", ".join(images))
        _ = builtins  # Keep the matching builtins DLL handles alive throughout compilation.


if __name__ == "__main__":
    main()
