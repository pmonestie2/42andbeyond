"""Known-build loopback data override for suspended, owned Net7Proxy processes."""
from __future__ import annotations

import ctypes
from ctypes import wintypes
import hashlib
import ipaddress
from pathlib import Path

PROXY_SHA256 = 'dbcece6433882234e842696713fef5454da91b90d3fa199e327e3301cd7faf87'

def configure_proxy_loopback(process: int, image: Path, address: str) -> None:
    """Give the suspended, owned 2.46 proxy a private localhost interface.

    /LADDRESS binds wildcard listeners in this build. Changing only the private
    default literal keeps HTTPS/global/master/sector listeners on one interface.
    The executable file and every instruction byte remain unchanged.

    Raises:
        ValueError: The address is outside the reserved instance loopbacks.
        RuntimeError: The proxy build or expected data literal differs.
        OSError: Windows cannot update the suspended proxy's data.
    """
    if (not ipaddress.IPv4Address(address).is_loopback or not address.startswith('127.0.0.')
            or not 2 <= int(address.rsplit('.', 1)[1]) <= 9):
        raise ValueError('Instance address must be 127.0.0.2 through 127.0.0.9')
    if hashlib.sha256(image.read_bytes()).hexdigest() != PROXY_SHA256:
        raise RuntimeError('Unsupported proxy build; no instance process resumed')
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    kernel.WriteProcessMemory.argtypes = kernel.ReadProcessMemory.argtypes
    kernel.VirtualProtectEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_size_t, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    kernel.ReadProcessMemory.restype = wintypes.BOOL
    kernel.WriteProcessMemory.restype = wintypes.BOOL
    kernel.VirtualProtectEx.restype = wintypes.BOOL
    pointer = 0x4402e8
    original = ctypes.create_string_buffer(10)
    count = ctypes.c_size_t()
    if (not kernel.ReadProcessMemory(process, pointer, original, 10, ctypes.byref(count))
            or count.value != 10 or original.raw != b'127.0.0.1\0'):
        raise RuntimeError('Proxy localhost literal differs; process will not run')
    previous = wintypes.DWORD()
    if not kernel.VirtualProtectEx(process, pointer, 10, 4, ctypes.byref(previous)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        value = ctypes.create_string_buffer(address.encode('ascii') + b'\0', 10)
        if not kernel.WriteProcessMemory(process, pointer, value, 10, ctypes.byref(count)) or count.value != 10:
            raise ctypes.WinError(ctypes.get_last_error())
        if (not kernel.ReadProcessMemory(process, pointer, original, 10, ctypes.byref(count))
                or count.value != 10 or original.raw != value.raw):
            raise RuntimeError('Proxy loopback readback mismatch; process will not run')
    finally:
        restored = wintypes.DWORD()
        if not kernel.VirtualProtectEx(process, pointer, 10, previous.value, ctypes.byref(restored)):
            raise ctypes.WinError(ctypes.get_last_error())
