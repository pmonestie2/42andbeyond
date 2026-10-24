"""Read Windows process identity without modifying its memory."""
import ctypes
from ctypes import wintypes


def process_path(handle: int) -> str:
    """Return the executable path for an already-open Windows process handle.

    Returns:
        str: Executable image path.

    Raises:
        OSError: Windows could not query the process image.
    """
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    query = kernel.QueryFullProcessImageNameW
    query.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    query.restype = wintypes.BOOL
    buffer = ctypes.create_unicode_buffer(32768)
    size = wintypes.DWORD(len(buffer))
    if not query(handle, 0, buffer, ctypes.byref(size)):
        raise OSError(ctypes.get_last_error(), 'Cannot read process image')
    return buffer.value
