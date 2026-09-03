#include <arpa/inet.h>
#include <errno.h>
#include <ifaddrs.h>
#include <net/if.h>
#include <net/route.h>
#include <netinet/in.h>
#include <netinet/ip.h>
#include <netinet/ip_icmp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <sys/sysctl.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

#define ROUNDUP(a) ((a) > 0 ? (1 + (((a) - 1) | (sizeof(long) - 1))) : sizeof(long))

static unsigned short checksum(const void *data, int length) {
    const unsigned short *words = (const unsigned short *)data;
    unsigned int sum = 0;
    while (length > 1) { sum += *words++; length -= 2; }
    if (length) sum += *(const unsigned char *)words;
    sum = (sum >> 16) + (sum & 0xffff);
    sum += sum >> 16;
    return (unsigned short)~sum;
}

static double now_ms(void) {
    struct timeval value;
    gettimeofday(&value, NULL);
    return value.tv_sec * 1000.0 + value.tv_usec / 1000.0;
}

static int snapshot(void) {
    int mib[] = { CTL_NET, PF_ROUTE, 0, AF_INET, NET_RT_DUMP, 0 };
    size_t length = 0;
    char *buffer = NULL;
    int first = 1;
    if (sysctl(mib, 6, NULL, &length, NULL, 0) == 0 && length > 0) {
        buffer = (char *)malloc(length);
        if (buffer && sysctl(mib, 6, buffer, &length, NULL, 0) == 0) {
            char *next = buffer;
            char *end = buffer + length;
            printf("{\"available\":true,\"gateways\":[");
            while (next < end) {
                struct rt_msghdr *message = (struct rt_msghdr *)next;
                if (message->rtm_msglen == 0) break;
                if ((message->rtm_flags & (RTF_UP | RTF_GATEWAY)) == (RTF_UP | RTF_GATEWAY)) {
                    struct sockaddr *sa = (struct sockaddr *)(message + 1);
                    struct sockaddr *addrs[RTAX_MAX] = {0};
                    for (int index = 0; index < RTAX_MAX; ++index) {
                        if (message->rtm_addrs & (1 << index)) { addrs[index] = sa; sa = (struct sockaddr *)((char *)sa + ROUNDUP(sa->sa_len)); }
                    }
                    if (addrs[RTAX_DST] && addrs[RTAX_GATEWAY] && addrs[RTAX_DST]->sa_family == AF_INET && addrs[RTAX_GATEWAY]->sa_family == AF_INET) {
                        struct sockaddr_in *dst = (struct sockaddr_in *)addrs[RTAX_DST];
                        struct sockaddr_in *gateway = (struct sockaddr_in *)addrs[RTAX_GATEWAY];
                        if (dst->sin_addr.s_addr == INADDR_ANY) {
                            char text[INET_ADDRSTRLEN];
                            if (inet_ntop(AF_INET, &gateway->sin_addr, text, sizeof(text))) {
                                if (!first) putchar(',');
                                printf("\"%s\"", text);
                                first = 0;
                            }
                        }
                    }
                }
                next += message->rtm_msglen;
            }
            printf("],\"interfaces\":[],\"detail\":\"macOS routing socket\"}\n");
            free(buffer);
            return 0;
        }
    }
    free(buffer);
    fprintf(stderr, "sysctl route failed: %s", strerror(errno));
    return 3;
}

typedef struct probe_result { int received; int reached; double milliseconds; char address[INET_ADDRSTRLEN]; } probe_result;

static probe_result ping_once(const struct sockaddr_in *destination, int timeout_ms, int ttl, unsigned short sequence) {
    probe_result result;
    memset(&result, 0, sizeof(result));
    int socket_fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_ICMP);
    if (socket_fd < 0) return result;
    setsockopt(socket_fd, IPPROTO_IP, IP_TTL, &ttl, sizeof(ttl));
    struct icmp packet;
    memset(&packet, 0, sizeof(packet));
    packet.icmp_type = ICMP_ECHO;
    packet.icmp_code = 0;
    packet.icmp_id = htons((unsigned short)(getpid() & 0xffff));
    packet.icmp_seq = htons(sequence);
    packet.icmp_cksum = checksum(&packet, sizeof(packet));
    double started = now_ms();
    if (sendto(socket_fd, &packet, sizeof(packet), 0, (const struct sockaddr *)destination, sizeof(*destination)) < 0) { close(socket_fd); return result; }
    fd_set readers;
    FD_ZERO(&readers); FD_SET(socket_fd, &readers);
    struct timeval timeout = { timeout_ms / 1000, (timeout_ms % 1000) * 1000 };
    if (select(socket_fd + 1, &readers, NULL, NULL, &timeout) > 0) {
        unsigned char response[2048];
        struct sockaddr_in source;
        socklen_t source_length = sizeof(source);
        ssize_t count = recvfrom(socket_fd, response, sizeof(response), 0, (struct sockaddr *)&source, &source_length);
        if (count > 0) {
            result.received = 1;
            result.milliseconds = now_ms() - started;
            inet_ntop(AF_INET, &source.sin_addr, result.address, sizeof(result.address));
            result.reached = source.sin_addr.s_addr == destination->sin_addr.s_addr;
        }
    }
    close(socket_fd);
    return result;
}

static int resolve_target(const char *text, struct sockaddr_in *destination) {
    memset(destination, 0, sizeof(*destination));
    destination->sin_len = sizeof(*destination);
    destination->sin_family = AF_INET;
    return inet_pton(AF_INET, text, &destination->sin_addr) == 1;
}

static int ping_command(const char *target, int count, int timeout_ms, int ttl) {
    struct sockaddr_in destination;
    if (!resolve_target(target, &destination)) { fprintf(stderr, "invalid IPv4 address"); return 2; }
    int received = 0;
    double total = 0;
    char responder[INET_ADDRSTRLEN] = "";
    for (int index = 0; index < count; ++index) {
        probe_result item = ping_once(&destination, timeout_ms, ttl > 0 ? ttl : 64, (unsigned short)index);
        if (item.received && item.reached) { received++; total += item.milliseconds; }
        if (item.address[0]) strcpy(responder, item.address);
    }
    printf("{\"available\":true,\"sent\":%d,\"received\":%d,\"averageMs\":", count, received);
    if (received) printf("%.1f", total / received); else printf("null");
    printf(",\"responder\":\"%s\",\"detail\":\"macOS ICMP datagram socket\"}\n", responder);
    return 0;
}

static int trace_command(const char *target, int max_hops, int timeout_ms) {
    struct sockaddr_in destination;
    if (!resolve_target(target, &destination)) { fprintf(stderr, "invalid IPv4 address"); return 2; }
    printf("{\"available\":true,\"hops\":[");
    for (int ttl = 1; ttl <= max_hops; ++ttl) {
        probe_result item = ping_once(&destination, timeout_ms, ttl, (unsigned short)ttl);
        if (ttl > 1) putchar(',');
        printf("{\"ttl\":%d,\"address\":\"%s\",\"averageMs\":", ttl, item.address);
        if (item.received) printf("%.1f", item.milliseconds); else printf("null");
        printf(",\"reached\":%s}", item.reached ? "true" : "false");
        if (item.reached) break;
    }
    printf("],\"detail\":\"macOS ICMP TTL\"}\n");
    return 0;
}

int main(int argc, char **argv) {
    if (argc >= 2 && strcmp(argv[1], "snapshot") == 0) return snapshot();
    if (argc >= 5 && strcmp(argv[1], "ping") == 0) return ping_command(argv[2], atoi(argv[3]), atoi(argv[4]), argc >= 6 ? atoi(argv[5]) : 0);
    if (argc >= 5 && strcmp(argv[1], "trace") == 0) return trace_command(argv[2], atoi(argv[3]), atoi(argv[4]));
    fprintf(stderr, "usage: netdiag-native snapshot | ping IPv4 count timeoutMs [ttl] | trace IPv4 maxHops timeoutMs");
    return 64;
}
