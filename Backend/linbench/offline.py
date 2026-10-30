"""LinBench keeps to this PC (same rule as TuneMe).

The window talks to LinBench over 127.0.0.1 and LinBench needs nothing else. After stay_on_this_pc()
any attempt made from this Python process to open a connection to another address, or to look a name
up on the network, fails at once with an error that says so, and is noted in BLOCKED.
"""
import socket

BLOCKED = []          # connections outside this PC that something tried to open (and did not get)


def _text(host):
    return host.decode('ascii', 'replace') if isinstance(host, bytes) else host


def _local(address):
    """True for an address on this PC (loopback)."""
    host = _text(address[0] if isinstance(address, tuple) and address else address)
    return isinstance(host, str) and (host in ('localhost', '::1', '') or host.startswith('127.'))


def _needs_no_lookup(host):
    """True for a name that is resolved on this PC without asking a name server."""
    import ipaddress

    host = _text(host)
    if host is None or host == '' or not isinstance(host, str):
        return True
    if host.lower() in ('localhost', socket.gethostname().lower()):
        return True
    try:
        ipaddress.ip_address(host.split('%')[0])
        return True          # a number, not a name: whether it may be reached is decided when connecting
    except ValueError:
        return False


def _refuse(what):
    if len(BLOCKED) < 50:
        BLOCKED.append(str(what))


def stay_on_this_pc():
    if getattr(socket.socket, '_linbench_local_only', False):
        return

    def guard(original):
        def connect(self, address, *args):
            if self.family in (socket.AF_INET, socket.AF_INET6) and not _local(address):
                _refuse(address)
                raise OSError(f'LinBench works offline: the connection to {address!r} was not opened.')
            return original(self, address, *args)
        return connect

    lookup = socket.getaddrinfo

    def getaddrinfo(host, *args, **kwargs):
        if not _needs_no_lookup(host):
            _refuse(host)
            raise socket.gaierror(socket.EAI_NONAME, f'LinBench works offline: {_text(host)!r} was not looked up.')
        return lookup(host, *args, **kwargs)

    socket.socket.connect = guard(socket.socket.connect)
    socket.socket.connect_ex = guard(socket.socket.connect_ex)
    socket.getaddrinfo = getaddrinfo
    socket.socket._linbench_local_only = True


def offline_works():
    """True if a connection outside this PC is refused.  Tried on an address reserved for documentation
    (192.0.2.1); without the guard nothing is tried at all, so nothing is ever contacted."""
    if not getattr(socket.socket, '_linbench_local_only', False):
        return False
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        s.connect(('192.0.2.1', 9))
    except OSError as e:
        refused = 'LinBench works offline' in str(e)
        if refused and BLOCKED and BLOCKED[-1] == str(('192.0.2.1', 9)):
            BLOCKED.pop()
        return refused
    finally:
        s.close()
    return False
