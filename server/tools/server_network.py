"""Address validation and registration probes for local development servers."""
import ipaddress
import socket
import struct


def sandbox_address(value: str) -> str:
    """Validate an IPv4 loopback or private LAN server address.

    Returns:
        str: The normalized address.

    Raises:
        ValueError: The address is not loopback or private LAN.
    """
    address = ipaddress.IPv4Address(value)
    ranges = ('127.0.0.0/8', '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')
    if not any(address in ipaddress.IPv4Network(block) for block in ranges):
        raise ValueError('Use a loopback or private LAN server address')
    return str(address)


def registered(server: str, timeout: float = .3) -> bool:
    """Returns:
        bool: Whether the server replies to the native registration probe.
    """
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as channel:
        channel.settimeout(timeout)
        channel.connect((server, 3806))
        channel.send(struct.pack('<HHIII', 16, 0x1000, 0, 0, 246))
        try:
            reply = channel.recv(1024)
        except (TimeoutError, ConnectionError, OSError):
            return False
    return len(reply) >= 12 and struct.unpack_from('<HHI', reply) == (len(reply), 0x1001, 0)
