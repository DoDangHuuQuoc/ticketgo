using EventTicketing.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketing.Modules.Promo;

/// Khách xem trạng thái VIP của mình (trang chọn vé, hồ sơ). Số tiền giảm thật được tính ở BookingService lúc đặt vé.
[ApiController, Authorize, Route("api/vip")]
public class VipController(VipService vip) : ControllerBase
{
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var st = await vip.StatusAsync(User.UserId());
        return Ok(new
        {
            st.Enabled, st.IsVip, st.Manual, st.Auto, st.Percent, st.MaxDiscount, st.Spent, st.AutoSpend,
            remaining = st.Enabled && !st.IsVip && st.AutoSpend > 0 ? Math.Max(0m, st.AutoSpend - st.Spent) : (decimal?)null
        });
    }
}
