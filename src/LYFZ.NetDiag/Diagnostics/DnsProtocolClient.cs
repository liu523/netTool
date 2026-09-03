using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LYFZ.NetDiag.Diagnostics;

internal static class DnsProtocolClient
{
    private const ushort QueryTypeA = 1;
    private const ushort QueryClassInternet = 1;

    public static async Task<DnsQueryResult> QueryAAsync(
        string host,
        IPAddress server,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var transactionId = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
            var packet = BuildQuery(host, transactionId);

            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(new IPEndPoint(server, 53));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await udp.SendAsync(packet.AsMemory(), timeoutCts.Token);
            var response = await udp.ReceiveAsync(timeoutCts.Token);
            stopwatch.Stop();

            return ParseResponse(server, transactionId, response.Buffer, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DnsQueryResult(server, false, stopwatch.Elapsed, "超时", []);
        }
        catch (Exception ex) when (ex is SocketException or IOException or FormatException)
        {
            return new DnsQueryResult(server, false, stopwatch.Elapsed, $"失败：{ex.Message}", []);
        }
    }

    private static byte[] BuildQuery(string host, ushort transactionId)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..2], transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0x0100); // Recursion desired.
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], 1);
        stream.Write(header);

        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new FormatException("域名标签长度无效。");
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question[0..2], QueryTypeA);
        BinaryPrimitives.WriteUInt16BigEndian(question[2..4], QueryClassInternet);
        stream.Write(question);
        return stream.ToArray();
    }

    private static DnsQueryResult ParseResponse(
        IPAddress server,
        ushort expectedTransactionId,
        byte[] packet,
        TimeSpan elapsed)
    {
        if (packet.Length < 12)
        {
            throw new FormatException("DNS响应过短。");
        }

        var transactionId = ReadUInt16(packet, 0);
        if (transactionId != expectedTransactionId)
        {
            throw new FormatException("DNS事务ID不匹配。");
        }

        var flags = ReadUInt16(packet, 2);
        var truncated = (flags & 0x0200) != 0;
        var responseCode = flags & 0x000F;
        var questionCount = ReadUInt16(packet, 4);
        var answerCount = ReadUInt16(packet, 6);
        var offset = 12;

        for (var i = 0; i < questionCount; i++)
        {
            _ = ReadName(packet, ref offset);
            EnsureAvailable(packet, offset, 4);
            offset += 4;
        }

        var answers = new List<DnsAnswer>();
        for (var i = 0; i < answerCount; i++)
        {
            _ = ReadName(packet, ref offset);
            EnsureAvailable(packet, offset, 10);
            var type = ReadUInt16(packet, offset);
            var recordClass = ReadUInt16(packet, offset + 2);
            var ttl = ReadUInt32(packet, offset + 4);
            var dataLength = ReadUInt16(packet, offset + 8);
            offset += 10;
            EnsureAvailable(packet, offset, dataLength);

            if (recordClass == QueryClassInternet && type == QueryTypeA && dataLength == 4)
            {
                answers.Add(new DnsAnswer("A", new IPAddress(packet.AsSpan(offset, 4)).ToString(), ttl));
            }
            else if (recordClass == QueryClassInternet && type == 5)
            {
                var nameOffset = offset;
                answers.Add(new DnsAnswer("CNAME", ReadName(packet, ref nameOffset), ttl));
            }

            offset += dataLength;
        }

        var status = responseCode switch
        {
            0 when truncated => "响应被截断(TC=1)",
            0 => "正常",
            1 => "格式错误(FORMERR)",
            2 => "服务器失败(SERVFAIL)",
            3 => "域名不存在(NXDOMAIN)",
            5 => "查询被拒绝(REFUSED)",
            _ => $"DNS错误码 {responseCode}"
        };

        return new DnsQueryResult(server, responseCode == 0, elapsed, status, answers);
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var current = offset;
        var jumped = false;
        var jumpCount = 0;

        while (true)
        {
            EnsureAvailable(packet, current, 1);
            var length = packet[current];
            if (length == 0)
            {
                current++;
                if (!jumped)
                {
                    offset = current;
                }

                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureAvailable(packet, current, 2);
                var pointer = ((length & 0x3F) << 8) | packet[current + 1];
                if (!jumped)
                {
                    offset = current + 2;
                }

                current = pointer;
                jumped = true;
                if (++jumpCount > 20)
                {
                    throw new FormatException("DNS名称压缩指针循环。");
                }

                continue;
            }

            if ((length & 0xC0) != 0 || length > 63)
            {
                throw new FormatException("DNS名称格式无效。");
            }

            current++;
            EnsureAvailable(packet, current, length);
            labels.Add(Encoding.ASCII.GetString(packet, current, length));
            current += length;
            if (!jumped)
            {
                offset = current;
            }
        }

        return string.Join('.', labels);
    }

    private static ushort ReadUInt16(byte[] packet, int offset)
    {
        EnsureAvailable(packet, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] packet, int offset)
    {
        EnsureAvailable(packet, offset, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offset, 4));
    }

    private static void EnsureAvailable(byte[] packet, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > packet.Length)
        {
            throw new FormatException("DNS响应数据不完整。");
        }
    }
}
