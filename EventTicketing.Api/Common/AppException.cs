using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EventTicketing.Common;

public class AppException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public class AppExceptionFilter(ILogger<AppExceptionFilter> log, IHostEnvironment env) : IExceptionFilter
{
    private static bool IsDbError(Exception? e)
    {
        for (; e is not null; e = e.InnerException) if (e is System.Data.Common.DbException) return true;
        return false;
    }

    public void OnException(ExceptionContext ctx)
    {
        if (ctx.Exception is AppException ex)
        {
            ctx.Result = new ObjectResult(new { code = ex.Code, message = ex.Message }) { StatusCode = ex.Status };
            ctx.ExceptionHandled = true;
            return;
        }

        var traceId = ctx.HttpContext.TraceIdentifier;
        log.LogError(ctx.Exception, "Unhandled ({TraceId}) {Method} {Path}", traceId, ctx.HttpContext.Request.Method, ctx.HttpContext.Request.Path);

        var db = IsDbError(ctx.Exception);
        ctx.Result = new ObjectResult(new
        {
            code = db ? "DB_ERROR" : "INTERNAL",
            message = db ? "Không kết nối được cơ sở dữ liệu. Vui lòng thử lại sau ít phút." : "Hệ thống đang gặp sự cố. Vui lòng thử lại sau.",
            traceId,
            detail = env.IsDevelopment() ? ctx.Exception.ToString() : null
        }) { StatusCode = db ? 503 : 500 };
        ctx.ExceptionHandled = true;
    }
}
