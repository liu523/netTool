#define _WIN32_WINNT 0x0601
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <icmpapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void print_json_string(const char *value) {
    const unsigned char *p = (const unsigned char *)(value ? value : "");
    putchar('"');
    while (*p) {
        if (*p == '"' || *p == '\\') { putchar('\\'); putchar(*p); }
        else if (*p < 0x20) printf("\\u%04x", *p);
        else putchar(*p);
        ++p;
    }
    putchar('"');
}

static int sockaddr_text(const SOCKADDR *address, char *buffer, DWORD length) {
    DWORD required = length;
    if (!address) return 0;
    buffer[0] = 0;
    return WSAAddressToStringA((LPSOCKADDR)address,
        address->sa_family == AF_INET ? sizeof(SOCKADDR_IN) : sizeof(SOCKADDR_IN6),
        NULL, buffer, &required) == 0;
}

static int snapshot(void) {
    ULONG size = 16384;
    IP_ADAPTER_ADDRESSES *addresses = NULL;
    ULONG status;
    int first_gateway = 1;
    int first_interface = 1;
    int attempts = 0;

    do {
        free(addresses);
        addresses = (IP_ADAPTER_ADDRESSES *)malloc(size);
        if (!addresses) { fprintf(stderr, "out of memory"); return 2; }
        status = GetAdaptersAddresses(AF_UNSPEC,
            GAA_FLAG_INCLUDE_GATEWAYS | GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST,
            NULL, addresses, &size);
    } while (status == ERROR_BUFFER_OVERFLOW && ++attempts < 3);

    if (status != NO_ERROR) {
        fprintf(stderr, "GetAdaptersAddresses failed: %lu", status);
        free(addresses);
        return 3;
    }

    printf("{\"available\":true,\"gateways\":[");
    for (IP_ADAPTER_ADDRESSES *adapter = addresses; adapter; adapter = adapter->Next) {
        if (adapter->OperStatus != IfOperStatusUp || adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK || adapter->IfType == IF_TYPE_TUNNEL) continue;
        for (IP_ADAPTER_GATEWAY_ADDRESS_LH *gateway = adapter->FirstGatewayAddress; gateway; gateway = gateway->Next) {
            char text[128];
            if (!sockaddr_text(gateway->Address.lpSockaddr, text, sizeof(text))) continue;
            if (!first_gateway) putchar(',');
            print_json_string(text);
            first_gateway = 0;
        }
    }
    printf("],\"interfaces\":[");
    for (IP_ADAPTER_ADDRESSES *adapter = addresses; adapter; adapter = adapter->Next) {
        if (adapter->OperStatus != IfOperStatusUp || adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK || adapter->IfType == IF_TYPE_TUNNEL) continue;
        if (!first_interface) putchar(',');
        printf("{\"index\":%lu,\"speed\":%llu}", adapter->IfIndex, (unsigned long long)adapter->TransmitLinkSpeed);
        first_interface = 0;
    }
    printf("],\"detail\":\"Windows IP Helper API\"}\n");
    free(addresses);
    return 0;
}

typedef struct ping_once_result {
    int received;
    DWORD status;
    DWORD milliseconds;
    IPAddr responder;
} ping_once_result;

static ping_once_result ping_once(HANDLE handle, IPAddr destination, DWORD timeout_ms, unsigned char ttl) {
    static const char payload[] = "LYFZ-NETDIAG";
    DWORD reply_size = sizeof(ICMP_ECHO_REPLY) + sizeof(payload) + 32;
    void *buffer = calloc(1, reply_size);
    IP_OPTION_INFORMATION options;
    ping_once_result result;
    memset(&result, 0, sizeof(result));
    result.status = IP_REQ_TIMED_OUT;
    memset(&options, 0, sizeof(options));
    options.Ttl = ttl;
    if (!buffer) { result.status = ERROR_OUTOFMEMORY; return result; }

    DWORD count = IcmpSendEcho(handle, destination, (LPVOID)payload, (WORD)sizeof(payload),
        &options, buffer, reply_size, timeout_ms);
    if (count > 0) {
        PICMP_ECHO_REPLY reply = (PICMP_ECHO_REPLY)buffer;
        result.received = 1;
        result.status = reply->Status;
        result.milliseconds = reply->RoundTripTime;
        result.responder = reply->Address;
    } else {
        result.status = GetLastError();
    }
    free(buffer);
    return result;
}

static int parse_ipv4(const char *text, IPAddr *address) {
    IN_ADDR parsed;
    if (InetPtonA(AF_INET, text, &parsed) != 1) return 0;
    *address = parsed.S_un.S_addr;
    return 1;
}

static void ipaddr_text(IPAddr address, char *buffer, size_t size) {
    IN_ADDR parsed;
    parsed.S_un.S_addr = address;
    if (!InetNtopA(AF_INET, &parsed, buffer, (DWORD)size)) strcpy_s(buffer, size, "");
}

static int ping_command(const char *target, int count, DWORD timeout_ms, int ttl) {
    IPAddr destination;
    if (!parse_ipv4(target, &destination)) { fprintf(stderr, "invalid IPv4 address"); return 2; }
    HANDLE handle = IcmpCreateFile();
    if (handle == INVALID_HANDLE_VALUE) { fprintf(stderr, "IcmpCreateFile failed: %lu", GetLastError()); return 3; }
    int received = 0;
    unsigned long long total = 0;
    DWORD last_status = IP_REQ_TIMED_OUT;
    char responder[64] = "";
    for (int i = 0; i < count; ++i) {
        ping_once_result item = ping_once(handle, destination, timeout_ms, (unsigned char)(ttl > 0 ? ttl : 128));
        last_status = item.status;
        if (item.received && item.status == IP_SUCCESS) { received++; total += item.milliseconds; }
        if (item.responder) ipaddr_text(item.responder, responder, sizeof(responder));
    }
    IcmpCloseHandle(handle);
    printf("{\"available\":true,\"sent\":%d,\"received\":%d,\"averageMs\":", count, received);
    if (received) printf("%.1f", (double)total / received); else printf("null");
    printf(",\"status\":%lu,\"responder\":", last_status); print_json_string(responder);
    printf(",\"detail\":\"Windows ICMP API\"}\n");
    return 0;
}

static int trace_command(const char *target, int max_hops, DWORD timeout_ms) {
    IPAddr destination;
    if (!parse_ipv4(target, &destination)) { fprintf(stderr, "invalid IPv4 address"); return 2; }
    HANDLE handle = IcmpCreateFile();
    if (handle == INVALID_HANDLE_VALUE) { fprintf(stderr, "IcmpCreateFile failed: %lu", GetLastError()); return 3; }
    printf("{\"available\":true,\"hops\":[");
    int first = 1;
    for (int ttl = 1; ttl <= max_hops; ++ttl) {
        int samples = 0;
        unsigned long long total = 0;
        int reached = 0;
        char responder[64] = "";
        DWORD status = IP_REQ_TIMED_OUT;
        for (int attempt = 0; attempt < 3; ++attempt) {
            ping_once_result item = ping_once(handle, destination, timeout_ms, (unsigned char)ttl);
            status = item.status;
            if (item.received) {
                samples++;
                total += item.milliseconds;
                if (item.responder) ipaddr_text(item.responder, responder, sizeof(responder));
                if (item.status == IP_SUCCESS) reached = 1;
            }
        }
        if (!first) putchar(',');
        printf("{\"ttl\":%d,\"address\":", ttl); print_json_string(responder);
        printf(",\"averageMs\":"); if (samples) printf("%.1f", (double)total / samples); else printf("null");
        printf(",\"status\":%lu,\"reached\":%s}", status, reached ? "true" : "false");
        first = 0;
        if (reached) break;
    }
    printf("],\"detail\":\"Windows ICMP TTL\"}\n");
    IcmpCloseHandle(handle);
    return 0;
}

int main(int argc, char **argv) {
    WSADATA data;
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) { fprintf(stderr, "WSAStartup failed"); return 1; }
    int code = 0;
    if (argc >= 2 && strcmp(argv[1], "snapshot") == 0) code = snapshot();
    else if (argc >= 6 && strcmp(argv[1], "ping") == 0) code = ping_command(argv[2], atoi(argv[3]), (DWORD)atoi(argv[4]), atoi(argv[5]));
    else if (argc >= 5 && strcmp(argv[1], "ping") == 0) code = ping_command(argv[2], atoi(argv[3]), (DWORD)atoi(argv[4]), 0);
    else if (argc >= 5 && strcmp(argv[1], "trace") == 0) code = trace_command(argv[2], atoi(argv[3]), (DWORD)atoi(argv[4]));
    else { fprintf(stderr, "usage: netdiag-native snapshot | ping IPv4 count timeoutMs [ttl] | trace IPv4 maxHops timeoutMs"); code = 64; }
    WSACleanup();
    return code;
}
