"""Probe a hardware encoder through the FFmpeg DLLs shipped beside vms.exe.

Answers "does this machine's driver encode with the FFmpeg we ship", without the stack: it opens
the D3D11VA and QSV devices, opens the encoder and encodes synthetic 1080p NV12 frames. FFmpeg's
verbose log, printed as it goes, names the MFX implementation that opened -- "hardware
accelerated" is the line to look for. It does not go through ta_roienc and decodes nothing.

usage: python -I qsv-probe.py <dir with avcodec-*.dll> [encoder, default h264_qsv] [frames]
"""
import ctypes as C
import glob
import os
import sys
import time

vms_dir = os.path.abspath(sys.argv[1])
encoder_name = sys.argv[2] if len(sys.argv) > 2 else "h264_qsv"
frames = int(sys.argv[3]) if len(sys.argv) > 3 else 100
W, H = 1920, 1080

os.add_dll_directory(vms_dir)


def load(prefix):
    path = glob.glob(os.path.join(vms_dir, prefix + "-*.dll"))[0]
    print("loading", os.path.basename(path))
    return C.CDLL(path)


avutil = load("avutil")
avcodec = load("avcodec")

avutil.av_version_info.restype = C.c_char_p
print("ffmpeg", avutil.av_version_info().decode())
avutil.av_log_set_level(40)  # AV_LOG_VERBOSE: QSV reports the MFX implementation it opened

avutil.av_strerror.argtypes = [C.c_int, C.c_char_p, C.c_size_t]


def err(code):
    buf = C.create_string_buffer(256)
    avutil.av_strerror(code, buf, 256)
    return f"{code} ({buf.value.decode()})"


# Hardware devices, as ta_roienc asks for them on Windows.
avutil.av_hwdevice_find_type_by_name.argtypes = [C.c_char_p]
avutil.av_hwdevice_ctx_create.argtypes = [C.POINTER(C.c_void_p), C.c_int, C.c_char_p, C.c_void_p, C.c_int]
for name in (b"d3d11va", b"qsv"):
    t = avutil.av_hwdevice_find_type_by_name(name)
    ref = C.c_void_p()
    rc = avutil.av_hwdevice_ctx_create(C.byref(ref), t, None, None, 0) if t > 0 else -1
    print(f"hwdevice {name.decode()}: type={t} ->", "OK" if rc == 0 else err(rc))

avcodec.avcodec_find_encoder_by_name.argtypes = [C.c_char_p]
avcodec.avcodec_find_encoder_by_name.restype = C.c_void_p
codec = avcodec.avcodec_find_encoder_by_name(encoder_name.encode())
if not codec:
    sys.exit(f"{encoder_name}: not in this FFmpeg build")

avcodec.avcodec_alloc_context3.argtypes = [C.c_void_p]
avcodec.avcodec_alloc_context3.restype = C.c_void_p
ctx = avcodec.avcodec_alloc_context3(codec)
avutil.av_opt_set.argtypes = [C.c_void_p, C.c_char_p, C.c_char_p, C.c_int]
for k, v in {"video_size": f"{W}x{H}", "pixel_format": "nv12", "time_base": "1/25",
             "b": "4000000", "g": "50"}.items():
    rc = avutil.av_opt_set(C.c_void_p(ctx), k.encode(), v.encode(), 0)
    if rc < 0:
        sys.exit(f"av_opt_set {k}: {err(rc)}")

avcodec.avcodec_open2.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p]
rc = avcodec.avcodec_open2(C.c_void_p(ctx), C.c_void_p(codec), None)
if rc < 0:
    sys.exit(f"avcodec_open2 {encoder_name}: {err(rc)}")
print(f"avcodec_open2 {encoder_name}: OK")

# AVFrame's leading fields have kept their layout for many major versions:
# data[8] @0, linesize[8] @64, extended_data @96, width @104, height @108, nb_samples @112, format @116.
avutil.av_frame_alloc.restype = C.c_void_p
avutil.av_frame_get_buffer.argtypes = [C.c_void_p, C.c_int]
avutil.av_frame_make_writable.argtypes = [C.c_void_p]
frame = avutil.av_frame_alloc()
C.c_int.from_address(frame + 104).value = W
C.c_int.from_address(frame + 108).value = H
C.c_int.from_address(frame + 116).value = 23  # AV_PIX_FMT_NV12
rc = avutil.av_frame_get_buffer(C.c_void_p(frame), 0)
if rc < 0:
    sys.exit(f"av_frame_get_buffer: {err(rc)}")

avcodec.av_packet_alloc.restype = C.c_void_p
avcodec.avcodec_send_frame.argtypes = [C.c_void_p, C.c_void_p]
avcodec.avcodec_receive_packet.argtypes = [C.c_void_p, C.c_void_p]
avcodec.av_packet_unref.argtypes = [C.c_void_p]
pkt = avcodec.av_packet_alloc()
EAGAIN, EOF = -11, -541478725
total_bytes = packets = 0


def drain():
    global total_bytes, packets
    while True:
        r = avcodec.avcodec_receive_packet(C.c_void_p(ctx), C.c_void_p(pkt))
        if r in (EAGAIN, EOF):
            return
        if r < 0:
            sys.exit(f"receive_packet: {err(r)}")
        total_bytes += C.c_int.from_address(pkt + 32).value  # AVPacket.size
        packets += 1
        avcodec.av_packet_unref(C.c_void_p(pkt))


t0 = time.perf_counter()
for i in range(frames):
    avutil.av_frame_make_writable(C.c_void_p(frame))
    y = C.c_void_p.from_address(frame + 0).value
    uv = C.c_void_p.from_address(frame + 8).value
    ls_y = C.c_int.from_address(frame + 64).value
    ls_uv = C.c_int.from_address(frame + 68).value
    C.memset(y, (i * 3) % 256, ls_y * H)          # moving luma so frames differ
    C.memset(uv, 128, ls_uv * H // 2)
    rc = avcodec.avcodec_send_frame(C.c_void_p(ctx), C.c_void_p(frame))
    if rc < 0:
        sys.exit(f"send_frame {i}: {err(rc)}")
    drain()
avcodec.avcodec_send_frame(C.c_void_p(ctx), None)
drain()
dt = time.perf_counter() - t0
print(f"encoded {frames} frames -> {packets} packets, {total_bytes} bytes in {dt:.2f}s ({frames / dt:.0f} fps)")
