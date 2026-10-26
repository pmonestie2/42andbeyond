"""Pure hosts-file transformations for the optional Windows proxy launcher."""

MARKER = b'42andbeyond-local-sandbox'
BOOTSTRAP_LINE = b'127.0.0.1\tlocal.net-7.org\t# 42andbeyond-proxy-bootstrap\r\n'


def profile_alias(line: bytes) -> bool:
    """Identify a named launcher's hosts-file alias.

    Returns:
        bool: Whether the line carries a named-instance marker.
    """
    return line.partition(b'#')[2].strip().startswith(b'42andbeyond-local-instance ')


def proxy_bootstrap_hosts(data: bytes) -> bytes:
    """Keep Net7Proxy's fixed bootstrap name resolvable while profiles are active.

    Returns:
        bytes: Hosts content preserving user mappings and removing only our exact
        bootstrap line after the last named instance exits.
    """
    lines = [line for line in data.splitlines(keepends=True)
             if line.rstrip(b'\r\n') != BOOTSTRAP_LINE.rstrip(b'\r\n')]
    base = b''.join(lines)
    if not any(profile_alias(line) for line in lines):
        return base
    for line in lines:
        fields = line.partition(b'#')[0].lower().split()
        if len(fields) >= 2 and b'local.net-7.org' in fields[1:]:
            return base
    separator = b'\r\n' if base and not base.endswith(b'\n') else b''
    return base + separator + BOOTSTRAP_LINE


def local_hosts(original: bytes, marker: bytes) -> bytes:
    """Replace only the local development hostname; keep other aliases intact."""
    if MARKER in original:
        raise RuntimeError('A local sandbox DNS override is already active; close that launcher first')
    output: list[bytes] = []
    for line in original.splitlines(keepends=True):
        if line.rstrip(b'\r\n') == BOOTSTRAP_LINE.rstrip(b'\r\n'):
            continue
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
        return proxy_bootstrap_hosts(original)

    def profile_line(line: bytes) -> bool:
        """Identify launcher profile entries for concurrent cleanup.

        Returns:
            bool: Whether this line carries a private-profile ownership marker.
        """
        return profile_alias(line) or line.rstrip(b'\r\n') == BOOTSTRAP_LINE.rstrip(b'\r\n')

    current_lines = current.splitlines(keepends=True)
    current_base = b''.join(line for line in current_lines if not profile_line(line))
    applied_base = b''.join(line for line in applied.splitlines(keepends=True) if not profile_line(line))
    if current_base.rstrip(b'\r\n') != applied_base.rstrip(b'\r\n'):
        return None
    base = b''.join(line for line in original.splitlines(keepends=True) if not profile_line(line))
    profiles = b''.join(line for line in current_lines if profile_alias(line))
    separator = b'\r\n' if profiles and base and not base.endswith(b'\n') else b''
    return proxy_bootstrap_hosts(base + separator + profiles)
