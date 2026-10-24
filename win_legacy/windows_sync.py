"""Serialize optional Windows launcher configuration updates."""
from contextlib import contextmanager
from collections.abc import Iterator


@contextmanager
def named_mutex(name: str, timeout: int = 15000) -> Iterator[None]:
    """Hold a named Windows mutex until the protected configuration change ends."""
    import win32api
    import win32event
    handle = win32event.CreateMutex(None, False, name)
    acquired = False
    try:
        result = win32event.WaitForSingleObject(handle, timeout)
        acquired = result in (win32event.WAIT_OBJECT_0, win32event.WAIT_ABANDONED)
        if not acquired: raise RuntimeError('This client profile is already running or another launch is preparing it')
        yield
    finally:
        if acquired: win32event.ReleaseMutex(handle)
        win32api.CloseHandle(handle)
