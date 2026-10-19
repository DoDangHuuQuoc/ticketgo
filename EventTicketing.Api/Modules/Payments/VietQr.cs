using System.Text;

namespace EventTicketing.Modules.Payments;

/// Sinh chuỗi VietQR (chuẩn EMVCo / NAPAS 247 – chuyển nhanh tới số tài khoản).
/// Mã sinh ra quét được bằng mọi app ngân hàng: tự điền sẵn ngân hàng, số tài khoản, SỐ TIỀN và NỘI DUNG chuyển khoản.
public static class VietQr
{
    public record Bank(string Bin, string Name);

    public static readonly Bank[] Banks =
    [
        new("970436", "Vietcombank"), new("970415", "VietinBank"), new("970418", "BIDV"), new("970405", "Agribank"),
        new("970407", "Techcombank"), new("970422", "MB Bank"), new("970416", "ACB"), new("970432", "VPBank"),
        new("970423", "TPBank"), new("970403", "Sacombank"), new("970437", "HDBank"), new("970441", "VIB"),
        new("970443", "SHB"), new("970426", "MSB"), new("970448", "OCB"), new("970440", "SeABank"),
        new("970449", "LPBank (LienVietPostBank)"), new("970431", "Eximbank"), new("970429", "SCB"), new("970425", "ABBANK"),
        new("970428", "Nam A Bank"), new("970409", "BacABank"), new("970412", "PVcomBank"), new("970419", "NCB"),
        new("970406", "DongA Bank"), new("970433", "VietBank"), new("970438", "BaoViet Bank"), new("970452", "KienLongBank"),
        new("970454", "VietCapitalBank"), new("970424", "Shinhan Bank"), new("970457", "Woori Bank"),
        new("546034", "CAKE by VPBank"), new("546035", "Ubank by VPBank"), new("963388", "Timo")
    ];

    public static string BankName(string bin) => Banks.FirstOrDefault(b => b.Bin == bin)?.Name ?? bin;

    private static string Tlv(string id, string value) => id + value.Length.ToString("00") + value;

    /// content: chỉ chữ/số/dấu cách, tối đa 25 ký tự (giới hạn của VietQR).
    public static string SanitizeContent(string content)
    {
        var sb = new StringBuilder();
        foreach (var ch in content.ToUpperInvariant())
            if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == ' ') sb.Append(ch);
        var s = sb.ToString().Trim();
        return s.Length > 25 ? s[..25] : s;
    }

    public static string Build(string bankBin, string accountNo, long amount, string content)
    {
        if (bankBin.Length != 6 || !bankBin.All(char.IsAsciiDigit)) throw new ArgumentException("BIN ngân hàng phải gồm 6 chữ số", nameof(bankBin));
        if (accountNo.Length is < 4 or > 19 || !accountNo.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Số tài khoản không hợp lệ", nameof(accountNo));
        if (amount <= 0) throw new ArgumentException("Số tiền phải lớn hơn 0", nameof(amount));
        content = SanitizeContent(content);

        var receiver = Tlv("00", bankBin) + Tlv("01", accountNo);                    // 00: BIN ngân hàng, 01: số tài khoản
        var merchant = Tlv("00", "A000000727") + Tlv("01", receiver) + Tlv("02", "QRIBFTTA");   // QRIBFTTA = chuyển nhanh tới tài khoản

        var sb = new StringBuilder();
        sb.Append(Tlv("00", "01"));                          // phiên bản payload
        sb.Append(Tlv("01", "12"));                          // 12 = mã động (có số tiền)
        sb.Append(Tlv("38", merchant));
        sb.Append(Tlv("53", "704"));                         // VND
        sb.Append(Tlv("54", amount.ToString()));             // số tiền (VND, không phần thập phân)
        sb.Append(Tlv("58", "VN"));
        if (content.Length > 0) sb.Append(Tlv("62", Tlv("08", content)));   // 08 = nội dung chuyển khoản
        sb.Append("6304");
        sb.Append(Crc16(sb.ToString()));
        return sb.ToString();
    }

    /// CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF) trên toàn bộ chuỗi gồm cả "6304".
    public static string Crc16(string s)
    {
        var crc = 0xFFFF;
        foreach (var b in Encoding.ASCII.GetBytes(s))
        {
            crc ^= b << 8;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return crc.ToString("X4");
    }
}
