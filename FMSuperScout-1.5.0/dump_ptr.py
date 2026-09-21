import ctypes
import sys

kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
handle = kernel32.OpenProcess(0x0010|0x0400, False, 47648)
if not handle:
    print(f"Failed to open process. Error: {ctypes.get_last_error()}")
    sys.exit(1)

buf = ctypes.create_string_buffer(256)
bytes_read = ctypes.c_size_t()

if kernel32.ReadProcessMemory(handle, ctypes.c_void_p(0x832030F740), buf, 256, ctypes.byref(bytes_read)):
    data = buf.raw[:bytes_read.value]
    print(f"Dump for RB London at 0x832030F740:")
    for i in range(0, len(data), 16):
        print(' '.join(f'{b:02X}' for b in data[i:i+16]))

if kernel32.ReadProcessMemory(handle, ctypes.c_void_p(0x831EEB4F90), buf, 256, ctypes.byref(bytes_read)):
    data = buf.raw[:bytes_read.value]
    print(f"\nDump for PSV at 0x831EEB4F90:")
    for i in range(0, len(data), 16):
        print(' '.join(f'{b:02X}' for b in data[i:i+16]))
