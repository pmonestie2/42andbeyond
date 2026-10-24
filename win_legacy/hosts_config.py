"""Pure hosts-file transformations for the optional Windows proxy launcher."""

MARKER = b'42andbeyond-local-sandbox'


def local_hosts(original: bytes, marker: bytes) -> bytes:
    """Replace only the local development hostname; keep other aliases intact."""
    if MARKER in original:
        raise RuntimeError('A local sandbox DNS override is already active; close that launcher first')
    output: list[bytes] = []
    for line in original.splitlines(keepends=True):
        content, separator, comment = line.partition(b'#')
        fields = content.split()
        if len(fields) >= 2 and any(name.lower() == b'local.net-7.org' for name in fields[1:]):
            aliases = [name for name in fields[1:] if name.lower() != b'local.net-7.org']
            if aliases:
                output.append(fields[0] + b'\t' + b' '.join(aliases) + (b' #' + comment.rstrip(b'\r\n') if separator else b'') + b'\r\n')
            elif separator:
                output.append(b'#' + comment.rstrip(b'\r\n') + b'\r\n')
        else:
            output.append(line)
    return b''.join(output).rstrip(b'\r\n') + b'\r\n127.0.0.1\tlocal.net-7.org\t# ' + marker + b'\r\n'


def restore_local_hosts(original: bytes, applied: bytes, current: bytes) -> bytes | None:
    """Undo the primary override while preserving currently active profile aliases.

    Returns:
        bytes | None: Restored content, or None if unrelated content changed.
    """
    if current == applied:
        return original

    def profile_line(line: bytes) -> bool:
        """Identify launcher profile entries for concurrent cleanup.

        Returns:
            bool: Whether this line carries a private-profile ownership marker.
        """
        return line.partition(b'#')[2].strip().startswith(b'42andbeyond-local-instance ')

    current_lines = current.splitlines(keepends=True)
    current_base = b''.join(line for line in current_lines if not profile_line(line))
    applied_base = b''.join(line for line in applied.splitlines(keepends=True) if not profile_line(line))
    if current_base.rstrip(b'\r\n') != applied_base.rstrip(b'\r\n'):
        return None
    base = b''.join(line for line in original.splitlines(keepends=True) if not profile_line(line))
    profiles = b''.join(line for line in current_lines if profile_line(line))
    separator = b'\r\n' if profiles and base and not base.endswith(b'\n') else b''
    return base + separator + profiles
