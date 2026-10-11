# 将 .ico 注入 exe 的 Win32 图标资源（.NET 8 SDK 不会把 ApplicationIcon 写入 apphost exe）。
# 用法：python inject_icon.py <exe路径> <ico路径>
# 原理：BeginUpdateResource + UpdateResource(RT_ICON/RT_GROUP_ICON) + EndUpdateResource，
#       与 ResourceHacker 等工具等效；仅新增图标资源，不影响 manifest 等既有资源。
import ctypes
import struct
import sys

# CI runner（如 GitHub windows-latest）控制台常为 cp1252，中文输出会抛 UnicodeEncodeError
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

BeginUpdateResource = kernel32.BeginUpdateResourceW
UpdateResource = kernel32.UpdateResourceW
EndUpdateResource = kernel32.EndUpdateResourceW

# 显式签名：type/name 传 ID 时必须是整数指针形式（IS_INTRESOURCE）
BeginUpdateResource.restype = ctypes.c_void_p
BeginUpdateResource.argtypes = [ctypes.c_wchar_p, ctypes.c_bool]
UpdateResource.restype = ctypes.c_bool
UpdateResource.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                           ctypes.c_uint16, ctypes.c_void_p, ctypes.c_uint32]
EndUpdateResource.restype = ctypes.c_bool
EndUpdateResource.argtypes = [ctypes.c_void_p, ctypes.c_bool]

RT_ICON = 3
RT_GROUP_ICON = 14


def main(exe_path: str, ico_path: str) -> int:
    data = open(ico_path, "rb").read()
    reserved, ico_type, count = struct.unpack_from("<HHH", data, 0)
    if ico_type != 1 or count == 0:
        print(f"Error: {ico_path} is not a valid ICO file")
        return 1

    # 解析目录条目并收集 RT_ICON 帧数据
    frames = []
    for i in range(count):
        w, h, colors, _, planes, bpp, size, offset = struct.unpack_from("<BBBBHHII", data, 6 + 16 * i)
        frames.append((w, h, bpp, data[offset:offset + size]))

    # 组装 RT_GROUP_ICON（GRPICONDIRENTRY 中资源 ID 从 1 开始）
    group = struct.pack("<HHH", 0, 1, count)
    for rid, (w, h, bpp, frame) in enumerate(frames, start=1):
        group += struct.pack("<BBBBHHII", w, h, 0, 0, 1, bpp, len(frame), rid)

    h = BeginUpdateResource(exe_path, False)
    if not h:
        print(f"Error: BeginUpdateResource failed (WinError {ctypes.get_last_error()})")
        return 1

    ok = True
    for rid, (w, hgt, bpp, frame) in enumerate(frames, start=1):
        buf = (ctypes.c_char * len(frame)).from_buffer_copy(frame)
        if not UpdateResource(h, ctypes.c_void_p(RT_ICON), ctypes.c_void_p(rid), 0, buf, len(frame)):
            print(f"Error: writing RT_ICON #{rid} failed (WinError {ctypes.get_last_error()})")
            ok = False
            break
    if ok:
        gbuf = (ctypes.c_char * len(group)).from_buffer_copy(group)
        if not UpdateResource(h, ctypes.c_void_p(RT_GROUP_ICON), ctypes.c_void_p(1), 0, gbuf, len(group)):
            print(f"Error: writing RT_GROUP_ICON failed (WinError {ctypes.get_last_error()})")
            ok = False

    if ok and not EndUpdateResource(h, False):
        print(f"Error: EndUpdateResource failed (WinError {ctypes.get_last_error()})")
        return 1
    if not ok:
        EndUpdateResource(h, True)  # 丢弃修改
        return 1

    print(f"Injected {len(frames)} icon frames into {exe_path}")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1], sys.argv[2]))
