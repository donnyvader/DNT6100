using Dnt.Inspections;
using Microsoft.Data.Sqlite;

namespace Dnt.JigDaily;

public sealed class DirectoryService(IConfiguration config,IWebHostEnvironment env)
{
    public string HubPath{get;}=Resolve(config["Storage:DbPath"]??"../7000/data/welding_item_tracker.db",env.ContentRootPath);
    public string ReportPath{get;}=Resolve(config["Integration:WorkReportDbPath"]??"../7007/data/WMonitor.WorkReport.db",env.ContentRootPath);
    public static string Resolve(string path,string root)=>Path.GetFullPath(Path.IsPathRooted(path)?path:Path.Combine(root,path));
    static SqliteConnection OpenReadOnly(string path)
    {
        if(!File.Exists(path)) throw new FileNotFoundException("연동 DB가 없습니다.");
        return new(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,DefaultTimeout=3}.ToString());
    }
    public async Task<IReadOnlyList<InspectionInspector>> Inspectors(CancellationToken ct=default)
    {
        if(!File.Exists(HubPath)) throw new JigException(503,"ITEM HUB 인원 DB에 연결할 수 없습니다.");
        try{return await InspectionInspectorDirectory.GetActiveAsync(new SqliteConnectionStringBuilder{DataSource=HubPath,Mode=SqliteOpenMode.ReadOnly}.ToString(),AdminDutyPermissions.JigDaily,ct);}
        catch(SqliteException){throw new JigException(503,"ITEM HUB 점검자 정보를 확인할 수 없습니다.");}
    }
    public async Task<Catalog> Catalog(CancellationToken ct=default)
    {
        try
        {
            await using var conn=OpenReadOnly(HubPath);await conn.OpenAsync(ct);
            await using var cmd=conn.CreateCommand();cmd.CommandText="""
                SELECT i.item_code,MAX(i.item_name),m.robot_no
                FROM items i
                LEFT JOIN machine_items mi ON mi.item_id=i.item_id AND mi.is_enabled=1
                LEFT JOIN machines m ON m.machine_id=mi.machine_id AND m.is_active=1
                    AND m.robot_no BETWEEN 1 AND 25
                WHERE i.is_active=1
                GROUP BY i.item_code,m.robot_no ORDER BY i.item_code,m.robot_no;
                """;
            var list=new List<(string Part,string Name,int? Robot)>();await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                try{list.Add((JigRules.Part(r.GetString(0)),r.GetString(1),r.IsDBNull(2)?null:r.GetInt32(2)));}catch(JigException){}
            }
            // 현재 활성 배정만 표시한다. 생산 이력의 호기는 현재 배정으로 대체하지 않는다.
            return new(true,"ITEM HUB 연결",list.GroupBy(x=>x.Part).Select(g=>new HubPart(g.Key,g.First().Name,
                g.Where(x=>x.Robot.HasValue).Select(x=>x.Robot!.Value).Distinct().Order().ToArray())).ToArray());
        }
        catch(Exception ex)when(ex is SqliteException or IOException){return new(false,"ITEM HUB 품번 연결 불가",[]);}
    }
    // 작업일보의 일자별 스냅샷을 읽기 전용으로 사용한다. 누적 생산 카운터나 JOB 배정은 생산 근거가 아니다.
    // 스냅샷 실적이 없는 날짜만 원시 생산 이벤트를 사용하여 두 자료의 중복 합산을 막는다.
    public async Task<ProductionRange> Production(string from,string to,CancellationToken ct=default)
    {
        JigRules.Date(from);JigRules.Date(to);
        try
        {
            await using var conn=OpenReadOnly(ReportPath);await conn.OpenAsync(ct);
            await using var transaction=(SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd=conn.CreateCommand();cmd.Transaction=transaction;
            cmd.CommandText="""
                WITH snapshot_days AS (
                  SELECT DISTINCT business_date FROM workreport_snapshot_items
                  WHERE business_date BETWEEN $from AND $to AND total_qty>0
                ), production AS (
                  SELECT business_date,machine_no,item_code,item_name,total_qty AS qty
                  FROM workreport_snapshot_items WHERE business_date BETWEEN $from AND $to AND total_qty>0
                  UNION ALL
                  SELECT business_date,machine_no,item_code,item_name,quantity AS qty
                  FROM workreport_events WHERE business_date BETWEEN $from AND $to
                  AND business_date NOT IN (SELECT business_date FROM snapshot_days)
                )
                SELECT business_date,machine_no,item_code,MAX(item_name),SUM(qty)
                FROM production GROUP BY business_date,machine_no,item_code HAVING SUM(qty)>0;
                """;
            cmd.Parameters.AddWithValue("$from",from);cmd.Parameters.AddWithValue("$to",to);
            var list=new List<ProductionPart>();await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                if(JigRules.Robot(r.GetString(1)) is not int robot) continue;
                try{list.Add(new(r.GetString(0),robot,JigRules.Part(r.GetString(2)),r.GetString(3),r.GetInt32(4)));}catch(JigException){}
            }
            return new(true,"작업일보 실적 연결",list.GroupBy(x=>(x.Date,x.Robot,x.Part)).Select(g=>g.First() with{Qty=g.Sum(x=>x.Qty)}).ToArray());
        }
        catch(Exception ex)when(ex is SqliteException or IOException){return new(false,"생산 확인 불가 · 작업일보 DB 연결을 확인하세요.",[]);}
    }
    public string? ImageUrl(string part)=>File.Exists(Path.Combine(env.WebRootPath,"jig-images",part+".jpg"))?"/jig-images/"+Uri.EscapeDataString(part)+".jpg":null;
    public IReadOnlyList<string> PhotoParts()
    {
        var path=Path.Combine(env.WebRootPath,"jig-images");if(!Directory.Exists(path))return [];
        return Directory.EnumerateFiles(path,"*.jpg").Select(Path.GetFileNameWithoutExtension)
            .Where(x=>System.Text.RegularExpressions.Regex.IsMatch(x??"",@"^[A-Z0-9][A-Z0-9_-]{1,79}$")).Select(x=>x!).ToArray();
    }
    public async Task<DailyBoard> Board(string date,JigStore store,CancellationToken ct=default)
    {
        JigRules.Date(date);
        var catalog=await Catalog(ct);var production=await Production(date,date,ct);var records=await store.List(date,date,ct);
        // 지그는 설비 간 이동할 수 있다. 호기 배정과 무관하게 품번/날짜 한 건을 모든 생산호기와 매칭한다.
        var keys=catalog.Parts.Select(x=>x.Part).Concat(PhotoParts()).Concat(production.Parts.Select(x=>x.Part))
            .Concat(records.Select(x=>x.Part)).Distinct().Order(StringComparer.Ordinal);
        var rows=new List<JigRow>();
        foreach(var key in keys)
        {
            var produced=production.Parts.Any(x=>x.Part==key&&x.Qty>0);
            var record=records.FirstOrDefault(x=>x.Part==key);
            var hubPart=catalog.Parts.FirstOrDefault(x=>x.Part==key);
            string name=hubPart?.Name??production.Parts.FirstOrDefault(x=>x.Part==key)?.Name??record?.PartName??"";
            string status=JigRules.Status(record);
            rows.Add(new(date,key,name,hubPart?.AssignedRobots??[],production.Available?produced:null,produced,status,JigRules.StatusText(status),
                record?.ApprovalStatus??"--",record?.InspectorName??"--",record?.Id,record?.Revision??0,ImageUrl(key)));
        }
        return new(date,production.Available,production.Message,catalog.Available,catalog.Message,rows);
    }
    public async Task<JigMonitorSnapshot> MonitorStatus(string date,JigStore store,CancellationToken ct=default)
    {
        JigRules.Date(date);
        var production=await Production(date,date,ct);
        if(!production.Available)return JigMonitorSnapshot.Unavailable(date);
        var records=await store.List(date,date,ct);
        var machines=Enumerable.Range(1,25).Select(robot=>
        {
            // 이 날짜의 실제 생산 품번으로만 필요 점검을 판단한다. 배정만 된 품번은 대상이 아니다.
            var parts=production.Parts.Where(x=>x.Robot==robot&&x.Qty>0).Select(x=>x.Part).Distinct().Order()
                .Select(part=>{var record=records.FirstOrDefault(x=>x.Part==part);
                    return new JigPartStatus(part,JigRules.Status(record),record?.ApprovalStatus??"NOT_SAVED");}).ToArray();
            int missing=parts.Count(x=>x.Status=="MISSING"),ng=parts.Count(x=>x.Status=="NG");
            string status=parts.Length==0?"NO_PRODUCTION":ng>0?"NG":missing>0?"PENDING":"DONE";
            string approval=parts.Length==0?"NOT_REQUIRED":parts.All(x=>x.ApprovalStatus=="APPROVED")?"APPROVED":"PENDING";
            return new JigMachineStatus($"R-{robot:00}",status,approval,parts.Length,parts.Length-missing,missing,ng,parts);
        }).ToArray();
        return new(date,true,"품번별 지그일상 점검 연결",machines);
    }
}
