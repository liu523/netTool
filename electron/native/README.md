# LYFZ NetDiag native probe

The Electron application never shells out to `ping`, `tracert`, `traceroute`, `nslookup`, `curl`, PowerShell, or a Unix shell.

The bundled helper exposes a deliberately small JSON command interface:

- `snapshot`: active default gateways
- `ping IPv4 count timeoutMs [ttl]`: ICMP echo through the operating-system API/socket
- `trace IPv4 maxHops timeoutMs`: ICMP TTL route sampling

Windows uses IP Helper/ICMP APIs available since Windows 7. macOS uses routing sysctl and ICMP datagram sockets. The helper accepts numeric IPv4 addresses only, so it never performs DNS resolution or executes another process.
