using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using Dnt.JigDaily;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using WeldingItemTracker.Services;

const string cookie="DNT_JIG_6100";
var builder=WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"]??"http://0.0.0.0:6100");
builder.Configuration["Storage:DbPath"]=DirectoryService.Resolve(builder.Configuration["Storage:DbPath"]??"../7000/data/welding_item_tracker.db",builder.Environment.ContentRootPath);
builder.Services.AddSingleton<DirectoryService>();builder.Services.AddSingleton<JigStore>();builder.Services.AddSingleton<AdminAuthService>();
builder.Services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("login",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=8,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
var app=builder.Build();_=app.Services.GetRequiredService<JigStore>();
app.UseRateLimiter();
app.Use(async(context,next)=>
{
    context.Response.Headers["X-Content-Type-Options"]="nosniff";context.Response.Headers["X-Frame-Options"]="DENY";
    context.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers.CacheControl="no-store";
    try
    {
        string path=context.Request.Path.Value??"";
        if(path.StartsWith("/api/",StringComparison.OrdinalIgnoreCase))
        {
            bool writing=context.Request.Method is not("GET" or "HEAD");string origin=context.Request.Headers.Origin.ToString();
            if(writing&&(context.Request.Headers["X-DNT-Jig"]!="1"||(!string.IsNullOrEmpty(origin)&&!string.Equals(origin,$"{context.Request.Scheme}://{context.Request.Host}",StringComparison.OrdinalIgnoreCase))))
                throw new JigException(403,"지그일상점검 화면에서 요청하세요.");
            if(path!="/api/auth/login")
            {
                var service=context.RequestServices.GetRequiredService<DirectoryService>();
                if(File.Exists(service.HubPath)&&!string.IsNullOrWhiteSpace(context.Request.Cookies[cookie]))
                    context.Items["JigAdmin"]=await context.RequestServices.GetRequiredService<AdminAuthService>().GetSessionAsync(context.Request.Cookies[cookie]);
                if(writing&&context.Items["JigAdmin"] is null)throw new JigException(401,"로그인 후 저장·결재할 수 있습니다.");
            }
        }
        await next();
    }
    catch(JigException ex){context.Response.StatusCode=ex.Status;await context.Response.WriteAsJsonAsync(new{message=ex.Message});}
    catch(Exception ex)when(ex is SqliteException or IOException){app.Logger.LogError(ex,"Jig daily data error");context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{message="데이터 연결을 확인하세요. 화면은 계속 사용할 수 있습니다."});}
});
app.UseDefaultFiles();app.UseStaticFiles();
app.MapGet("/api/health",()=>new{status="ok",port=6100,version="1.0.5"});
app.MapGet("/api/definitions",()=>JigRules.Checks);
app.MapGet("/api/inspectors",async(DirectoryService directory,CancellationToken ct)=>await directory.Inspectors(ct));
app.MapGet("/api/board",async(string date,DirectoryService directory,JigStore store,CancellationToken ct)=>await directory.Board(date,store,ct));
app.MapGet("/api/monitor-status",async(string date,DirectoryService directory,JigStore store,CancellationToken ct)=>await directory.MonitorStatus(date,store,ct));
app.MapGet("/api/inspection",async(string date,string part,JigStore store,CancellationToken ct)=>Results.Ok(await store.Get(date,part,ct)));
app.MapPost("/api/inspection",async(SaveInspection request,HttpContext ctx,JigStore store,CancellationToken ct)=>await store.Save(request,Actor(ctx),ct));
app.MapPost("/api/inspection/approve/{stage}",async(string stage,string date,string part,ApprovalRequest request,HttpContext ctx,JigStore store,CancellationToken ct)=>await store.Approve(date,part,stage,request,Actor(ctx),ct));
app.MapGet("/api/history/{id:long}",async(long id,JigStore store,CancellationToken ct)=>await store.History(id,ct));
app.MapGet("/api/records",async(string? date,string? month,JigStore store,CancellationToken ct)=>{var range=Range(date,month);return await store.List(range.From,range.To,ct);});
app.MapGet("/api/summary",async(string date,DirectoryService directory,JigStore store,CancellationToken ct)=>
{
    var board=await directory.Board(date,store,ct);
    return new{date,productionAvailable=board.ProductionAvailable,parts=board.Rows};
});
app.MapGet("/api/export",async(string? date,string? month,JigStore store,CancellationToken ct)=>
{
    var range=Range(date,month);var rows=await store.List(range.From,range.To,ct);
    var sb=new StringBuilder("점검일\t품번\t품명\t점검자\t판정\t결재\t");sb.AppendJoin('\t',JigRules.Checks.Select(x=>x.Name));sb.AppendLine("\t비고");
    foreach(var row in rows)sb.AppendLine(string.Join('\t',new[]{row.Date,row.Part,row.PartName,row.InspectorName,row.Overall,ApprovalText(row.ApprovalStatus)}
        .Concat(JigRules.Checks.Select(x=>row.Checks.First(c=>c.Code==x.Code).Result)).Append(row.Notes).Select(Cell)));
    // UTF-16 TSV: Excel에서 한글이 유지되고 셀 문자열이 수식으로 실행되지 않도록 처리한다.
    return Results.File(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(sb.ToString())).ToArray(),"text/tab-separated-values; charset=utf-16",$"지그일상점검_{range.From}_{range.To}.tsv");
});
app.MapPost("/api/auth/login",async(AdminLoginPayload request,HttpContext ctx,AdminAuthService auth,DirectoryService directory)=>
{
    if(!File.Exists(directory.HubPath))throw new JigException(503,"ITEM HUB 인원 DB에 연결할 수 없습니다.");
    var (admin,token)=await auth.LoginAsync(request.LoginId,request.Password,ctx.Connection.RemoteIpAddress?.ToString(),ctx.Request.Headers.UserAgent.ToString());
    if(admin is null||token is null)throw new JigException(401,"아이디 또는 비밀번호를 확인하세요.");
    ctx.Response.Cookies.Append(cookie,token,new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Strict,Secure=ctx.Request.IsHttps,Path="/",Expires=admin.ExpiresAt,IsEssential=true});
    return Session(admin);
}).RequireRateLimiting("login");
app.MapGet("/api/auth/status",(HttpContext ctx)=>Session(ctx.Items["JigAdmin"] as AdminSession));
app.MapPost("/api/auth/logout",async(HttpContext ctx,AdminAuthService auth)=>{await auth.LogoutAsync(ctx.Request.Cookies[cookie],Actor(ctx),ctx.Connection.RemoteIpAddress?.ToString());ctx.Response.Cookies.Delete(cookie,new CookieOptions{Path="/"});return Results.Ok();});
app.Map("/api/{**path}",()=>Results.NotFound());app.MapFallbackToFile("index.html");app.Run();
static AdminSession Actor(HttpContext ctx)=>(AdminSession)ctx.Items["JigAdmin"]!;
static object Session(AdminSession? admin)=>admin is null?new{loggedIn=false}:(object)new{loggedIn=true,admin.Id,admin.DisplayName,admin.RoleCode,admin.IsMaster};
static(string From,string To) Range(string? date,string? month)
{
    if(!string.IsNullOrWhiteSpace(month))
    {
        if(!DateOnly.TryParseExact(month+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var first))throw new JigException(400,"조회 월을 확인하세요.");
        return(first.ToString("yyyy-MM-dd"),first.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"));
    }
    var day=JigRules.Date(date);return(day.ToString("yyyy-MM-dd"),day.ToString("yyyy-MM-dd"));
}
static string ApprovalText(string code)=>code switch{"APPROVED"=>"승인 완료","STAFF_APPROVED"=>"책임자 대기",_=>"담당자 대기"};
static string Cell(string value)
{
    value=value.Replace('\t',' ').Replace('\r',' ').Replace('\n',' ');
    if(value.TrimStart().StartsWith('=')||value.TrimStart().StartsWith('+')||value.TrimStart().StartsWith('-')||value.TrimStart().StartsWith('@'))value="'"+value;
    return value;
}
