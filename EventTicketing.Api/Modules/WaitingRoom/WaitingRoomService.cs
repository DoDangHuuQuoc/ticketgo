using EventTicketing.Common;

namespace EventTicketing.Modules.WaitingRoom;

/// Trạng thái của một người trong phòng chờ của một sự kiện.
/// Status: "admitted" (đang có suất đặt vé) | "waiting" (đang xếp hàng) | "none" (chưa vào hàng / đã bị loại do không phản hồi).
public record QueueStatus(string Status, int Position, int Waiting, int Active, int Capacity, int EtaSeconds, int PollSeconds, int SlotLeftSeconds = 0);

public record QueueRoomInfo(Guid EventId, int Active, int Waiting);

/// Hàng chờ mua vé theo từng sự kiện (xếp hàng thật, không phải chờ giả lập).
/// Mỗi sự kiện chỉ cho tối đa Queue:MaxActive người "đang mua vé" cùng lúc; khi quá đông, người đến sau phải xếp hàng
/// (vào trước ra trước) và chỉ được vào mua khi tới lượt.
/// - Người đang mua phải gửi tín hiệu (status) định kỳ; im lặng quá Queue:ActiveIdleSeconds (mặc định 45 giây) thì mất suất để nhường người kế tiếp.
/// - Suất mua vé tối đa Queue:SlotSeconds (mặc định 600 giây) hoặc hết ngay khi khách giữ vé xong / rời hàng.
/// - Người đang chờ cũng phải gọi /status định kỳ; im lặng quá Queue:WaitTimeoutSeconds (mặc định 45 giây) thì bị loại khỏi hàng.
/// Lưu trong bộ nhớ của API (1 tiến trình): khởi động lại API thì hàng chờ được làm mới.
public sealed class WaitingRoomService(IConfiguration cfg)
{
    private sealed class Room
    {
        public readonly object Gate = new();
        public readonly Dictionary<Guid, (DateTime Expires, DateTime LastSeen)> Active = new();                // userId -> (hết hạn suất, lần cuối còn hoạt động)
        public readonly LinkedList<Guid> Line = new();                                                    // thứ tự chờ
        public readonly Dictionary<Guid, (LinkedListNode<Guid> Node, DateTime LastSeen)> Waiting = new(); // userId -> vị trí trong Line
        public readonly Queue<DateTime> Admits = new();                                                   // thời điểm được vào (60 giây gần nhất) để ước lượng thời gian chờ
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Room> rooms = new();

    public bool Enabled => cfg.GetValue("Queue:Enabled", true);
    public int Capacity => Math.Max(1, cfg.GetValue("Queue:MaxActive", 150));
    public int SlotSeconds => Math.Max(60, cfg.GetValue("Queue:SlotSeconds", 600));
    public int ActiveIdleSeconds => Math.Max(15, cfg.GetValue("Queue:ActiveIdleSeconds", 45));
    public bool BypassAdmin => cfg.GetValue("Queue:BypassAdmin", false);
    public int WaitTimeoutSeconds => Math.Max(15, cfg.GetValue("Queue:WaitTimeoutSeconds", 45));

    private Room RoomOf(Guid eventId) => rooms.GetOrAdd(eventId, _ => new Room());

    private static void RemoveWaiting(Room r, Guid user)
    {
        if (r.Waiting.Remove(user, out var w)) r.Line.Remove(w.Node);
    }

    private void Promote(Room r, DateTime now)
    {
        var cap = Capacity;
        while (r.Active.Count < cap && r.Line.First is not null)
        {
            var user = r.Line.First.Value;
            r.Line.RemoveFirst(); r.Waiting.Remove(user);
            r.Active[user] = (now.AddSeconds(SlotSeconds), now);
            r.Admits.Enqueue(now);
        }
    }

    // Dọn người hết hạn / bỏ đi, rồi cho người kế tiếp vào nếu còn suất. Gọi trong lock.
    private void Tidy(Room r, DateTime now)
    {
        var idle = TimeSpan.FromSeconds(ActiveIdleSeconds);
        foreach (var k in r.Active.Where(x => x.Value.Expires <= now || now - x.Value.LastSeen > idle).Select(x => x.Key).ToList()) r.Active.Remove(k);
        var timeout = TimeSpan.FromSeconds(WaitTimeoutSeconds);
        foreach (var k in r.Waiting.Where(x => now - x.Value.LastSeen > timeout).Select(x => x.Key).ToList()) RemoveWaiting(r, k);
        while (r.Admits.Count > 0 && now - r.Admits.Peek() > TimeSpan.FromSeconds(60)) r.Admits.Dequeue();
        Promote(r, now);
    }

    private QueueStatus Build(Room r, Guid user)
    {
        if (r.Active.TryGetValue(user, out var a))
            return new QueueStatus("admitted", 0, r.Line.Count, r.Active.Count, Capacity, 0, 10, Math.Max(0, (int)(a.Expires - DateTime.UtcNow).TotalSeconds));
        if (r.Waiting.TryGetValue(user, out var w))
        {
            var pos = 1;
            for (var n = r.Line.First; n is not null && n != w.Node; n = n.Next) pos++;
            var rate = Math.Max(r.Admits.Count, 1);   // số người được vào trong 60 giây gần nhất (tối thiểu 1)
            var eta = (int)Math.Min(7200, Math.Ceiling(pos * 60.0 / rate));
            return new QueueStatus("waiting", pos, r.Line.Count, r.Active.Count, Capacity, eta, pos <= 20 ? 3 : 5);
        }
        return new QueueStatus("none", 0, r.Line.Count, r.Active.Count, Capacity, 0, 3);
    }

    /// Vào phòng chờ: có suất trống và không ai đang xếp hàng thì vào thẳng, ngược lại xếp cuối hàng.
    public QueueStatus Join(Guid eventId, Guid userId)
    {
        var r = RoomOf(eventId); var now = DateTime.UtcNow;
        lock (r.Gate)
        {
            if (!Enabled) return new QueueStatus("admitted", 0, 0, r.Active.Count, Capacity, 0, 5);
            Tidy(r, now);
            if (r.Active.TryGetValue(userId, out var act)) { r.Active[userId] = (act.Expires, now); return Build(r, userId); }
            if (r.Waiting.TryGetValue(userId, out var w)) { r.Waiting[userId] = (w.Node, now); return Build(r, userId); }
            if (r.Active.Count < Capacity && r.Line.Count == 0) { r.Active[userId] = (now.AddSeconds(SlotSeconds), now); r.Admits.Enqueue(now); }
            else r.Waiting[userId] = (r.Line.AddLast(userId), now);
            return Build(r, userId);
        }
    }

    /// Hỏi vị trí hiện tại (đồng thời báo "tôi vẫn còn ở đây" để không bị loại khỏi hàng).
    public QueueStatus Status(Guid eventId, Guid userId)
    {
        var r = RoomOf(eventId); var now = DateTime.UtcNow;
        lock (r.Gate)
        {
            if (!Enabled) return new QueueStatus("admitted", 0, 0, r.Active.Count, Capacity, 0, 5);
            if (r.Waiting.TryGetValue(userId, out var w)) r.Waiting[userId] = (w.Node, now);
            Tidy(r, now);   // người đang mua đã im lặng quá hạn thì mất suất trước, không được "cứu" bằng lần gọi muộn
            if (r.Active.TryGetValue(userId, out var a)) r.Active[userId] = (a.Expires, now);
            return Build(r, userId);
        }
    }

    /// Trả suất (đã giữ vé xong / rời hàng chờ) để người kế tiếp được vào.
    public void Release(Guid eventId, Guid userId)
    {
        var r = RoomOf(eventId);
        lock (r.Gate) { r.Active.Remove(userId); RemoveWaiting(r, userId); Promote(r, DateTime.UtcNow); }
    }

    /// Chặn đặt vé khi người dùng chưa được vào. Còn suất trống và không ai chờ thì tự cho vào (client cũ không biết hàng chờ vẫn dùng được).
    public void RequireAdmitted(Guid eventId, Guid userId)
    {
        if (!Enabled) return;
        var r = RoomOf(eventId); var now = DateTime.UtcNow;
        lock (r.Gate)
        {
            Tidy(r, now);
            if (r.Active.TryGetValue(userId, out var act)) { r.Active[userId] = (act.Expires, now); return; }
            if (r.Active.Count < Capacity && r.Line.Count == 0) { r.Active[userId] = (now.AddSeconds(SlotSeconds), now); r.Admits.Enqueue(now); return; }
            throw new AppException(429, "QUEUE_WAIT", "Hiện có rất đông người đang đặt vé. Bạn cần xếp hàng chờ để được phục vụ lần lượt.");
        }
    }

    /// Cho trang admin: các sự kiện đang có người đặt vé hoặc xếp hàng.
    public List<QueueRoomInfo> Snapshot()
    {
        var now = DateTime.UtcNow; var list = new List<QueueRoomInfo>();
        foreach (var (eventId, r) in rooms)
            lock (r.Gate)
            {
                Tidy(r, now);
                if (r.Active.Count + r.Line.Count > 0) list.Add(new QueueRoomInfo(eventId, r.Active.Count, r.Line.Count));
            }
        return list.OrderByDescending(x => x.Waiting).ThenByDescending(x => x.Active).ToList();
    }
}
