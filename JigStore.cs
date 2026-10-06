using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeldingItemTracker.Services;

namespace Dnt.JigDaily;

public sealed class JigStore
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    readonly string connectionString;
    readonly SemaphoreSlim gate=new(1,1);
    readonly DirectoryService directory;
    public JigStore(IConfiguration config,IWebHostEnvironment env,DirectoryService directory)
    {
        this.directory=directory;
        var path=DirectoryService.Resolve(config["JigDaily:DbPath"]??"data/jig_daily.db",env.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString=new SqliteConnectionStringBuilder{DataSource=path,DefaultTimeout=5}.ToString();
        using var conn=new SqliteConnection(connectionString);conn.Open();using var cmd=conn.CreateCommand();
        cmd.CommandText="""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS jig_part_inspections(
                id INTEGER PRIMARY KEY AUTOINCREMENT,date TEXT NOT NULL,
                part TEXT NOT NULL,revision INTEGER NOT NULL,document TEXT NOT NULL,UNIQUE(date,part));
            CREATE TABLE IF NOT EXISTS jig_part_history(
                id INTEGER PRIMARY KEY AUTOINCREMENT,inspection_id INTEGER NOT NULL,revision INTEGER NOT NULL,
                action TEXT NOT NULL,at TEXT NOT NULL,actor_id INTEGER NOT NULL,actor TEXT NOT NULL,document TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_jig_part_date ON jig_part_inspections(date,part);
            CREATE INDEX IF NOT EXISTS ix_jig_part_history ON jig_part_history(inspection_id,id);
            CREATE TABLE IF NOT EXISTS jig_part_schema(version INTEGER PRIMARY KEY);
            """;
        cmd.ExecuteNonQuery();
        MigrateLegacy(conn);
    }
    static void MigrateLegacy(SqliteConnection conn)
    {
        using var tx=conn.BeginTransaction();using var cmd=conn.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT COUNT(*) FROM jig_part_schema WHERE version=1;";
        if(Convert.ToInt32(cmd.ExecuteScalar())>0){tx.Commit();return;}
        cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='jig_inspections';";
        if(Convert.ToInt32(cmd.ExecuteScalar())>0)
        {
            var legacy=new List<JigRecord>();cmd.CommandText="SELECT document FROM jig_inspections;";
            using(var reader=cmd.ExecuteReader())while(reader.Read())legacy.Add(JsonSerializer.Deserialize<JigRecord>(reader.GetString(0),Json)!);
            foreach(var group in legacy.GroupBy(x=>(x.Date,x.Part)))
            {
                var chosen=group.OrderByDescending(x=>x.UpdatedAt,StringComparer.Ordinal).ThenByDescending(x=>x.Id).First();
                // 기존 호기별 자료는 삭제하지 않는다. 중복 품번은 모든 이력을 연결하고 새 기준으로 재결재한다.
                if(group.Count()>1)chosen=chosen with{Revision=group.Max(x=>x.Revision)+1,ApprovalStatus="PENDING",Approvals=[]};
                cmd.CommandText="INSERT INTO jig_part_inspections(id,date,part,revision,document) VALUES($id,$date,$part,$rev,$doc);";
                cmd.Parameters.Clear();cmd.Parameters.AddWithValue("$id",chosen.Id);cmd.Parameters.AddWithValue("$date",chosen.Date);
                cmd.Parameters.AddWithValue("$part",chosen.Part);cmd.Parameters.AddWithValue("$rev",chosen.Revision);cmd.Parameters.AddWithValue("$doc",JsonSerializer.Serialize(chosen,Json));cmd.ExecuteNonQuery();
                foreach(var previous in group)
                {
                    cmd.CommandText="INSERT INTO jig_part_history(inspection_id,revision,action,at,actor_id,actor,document) SELECT $id,revision,action,at,actor_id,actor,document FROM jig_history WHERE inspection_id=$oldId;";
                    cmd.Parameters.Clear();cmd.Parameters.AddWithValue("$id",chosen.Id);cmd.Parameters.AddWithValue("$oldId",previous.Id);cmd.ExecuteNonQuery();
                }
                cmd.CommandText="INSERT INTO jig_part_history(inspection_id,revision,action,at,actor_id,actor,document) VALUES($id,$rev,'PART_MIGRATION',$at,0,'시스템 전환',$doc);";
                cmd.Parameters.Clear();cmd.Parameters.AddWithValue("$id",chosen.Id);cmd.Parameters.AddWithValue("$rev",chosen.Revision);cmd.Parameters.AddWithValue("$at",DateTimeOffset.Now.ToString("O"));cmd.Parameters.AddWithValue("$doc",JsonSerializer.Serialize(chosen,Json));cmd.ExecuteNonQuery();
            }
        }
        cmd.CommandText="INSERT INTO jig_part_schema(version) VALUES(1);";cmd.Parameters.Clear();cmd.ExecuteNonQuery();tx.Commit();
    }
    public async Task<IReadOnlyList<JigRecord>> List(string from,string to,CancellationToken ct=default)
    {
        JigRules.Date(from);JigRules.Date(to);await using var conn=new SqliteConnection(connectionString);await conn.OpenAsync(ct);
        await using var cmd=conn.CreateCommand();cmd.CommandText="SELECT document FROM jig_part_inspections WHERE date BETWEEN $from AND $to ORDER BY date DESC,part;";
        cmd.Parameters.AddWithValue("$from",from);cmd.Parameters.AddWithValue("$to",to);
        var list=new List<JigRecord>();await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))list.Add(JsonSerializer.Deserialize<JigRecord>(r.GetString(0),Json)!);return list;
    }
    static async Task<JigRecord?> Find(SqliteConnection conn,SqliteTransaction? tx,string date,string part,CancellationToken ct)
    {
        await using var cmd=conn.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT document FROM jig_part_inspections WHERE date=$date AND part=$part;";
        cmd.Parameters.AddWithValue("$date",date);cmd.Parameters.AddWithValue("$part",part);
        var json=await cmd.ExecuteScalarAsync(ct) as string;return json is null?null:JsonSerializer.Deserialize<JigRecord>(json,Json);
    }
    public async Task<JigRecord?> Get(string date,string part,CancellationToken ct=default)
    {
        JigRules.Date(date);part=JigRules.Part(part);
        await using var conn=new SqliteConnection(connectionString);await conn.OpenAsync(ct);return await Find(conn,null,date,part,ct);
    }
    public async Task<IReadOnlyList<JigHistory>> History(long id,CancellationToken ct=default)
    {
        await using var conn=new SqliteConnection(connectionString);await conn.OpenAsync(ct);await using var cmd=conn.CreateCommand();
        cmd.CommandText="SELECT revision,action,at,actor,document FROM jig_part_history WHERE inspection_id=$id ORDER BY id DESC;";cmd.Parameters.AddWithValue("$id",id);
        var list=new List<JigHistory>();await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))list.Add(new(r.GetInt32(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4)));return list;
    }
    public async Task<JigRecord> Save(SaveInspection request,AdminSession actor,CancellationToken ct=default)
    {
        var date=JigRules.Date(request.Date);var part=JigRules.Part(request.Part);
        if(date>DateOnly.FromDateTime(DateTime.Now))throw new JigException(400,"점검일을 확인하세요.");
        string notes=(request.Notes??"").Trim();if(notes.Length>2000)throw new JigException(400,"비고는 2,000자 이내로 입력하세요.");
        var checks=request.Checks??[];
        if(checks.Count!=JigRules.Checks.Length||checks.Select(x=>x.Code).Distinct().Count()!=JigRules.Checks.Length||
            JigRules.Checks.Any(d=>!checks.Any(x=>x.Code==d.Code))||checks.Any(x=>x.Result is not("OK" or "NG")))
            throw new JigException(400,"7개 점검항목을 모두 OK 또는 NG로 선택하세요.");
        if(checks.Any(x=>(x.Note??"").Length>500))throw new JigException(400,"항목별 메모는 500자 이내로 입력하세요.");
        if(checks.Any(x=>x.Result=="NG"&&string.IsNullOrWhiteSpace(x.Note))&&string.IsNullOrWhiteSpace(notes))
            throw new JigException(400,"NG 항목의 조치 내용을 입력하세요.");
        var inspectors=await directory.Inspectors(ct);var inspector=inspectors.FirstOrDefault(x=>x.AdminId==request.InspectorId)
            ??throw new JigException(400,"지그일상점검 권한이 있는 점검자를 선택하세요.");
        if(!actor.IsMaster&&!inspectors.Any(x=>x.AdminId==actor.Id))throw new JigException(403,"지그일상점검 입력 권한이 없습니다.");
        if(!actor.IsMaster&&actor.Id!=inspector.AdminId)throw new JigException(403,"본인 이름으로 점검을 저장하세요.");
        var catalog=await directory.Catalog(ct);var production=await directory.Production(request.Date,request.Date,ct);
        await gate.WaitAsync(ct);
        try
        {
            await using var conn=new SqliteConnection(connectionString);await conn.OpenAsync(ct);
            await using var tx=(SqliteTransaction)await conn.BeginTransactionAsync(ct);
            var old=await Find(conn,tx,request.Date,part,ct);
            if(!catalog.Parts.Any(x=>x.Part==part)&&!production.Parts.Any(x=>x.Part==part)&&directory.ImageUrl(part) is null&&old is null)
                throw new JigException(400,"등록된 지그 품번이 아닙니다.");
            if((old?.Revision??0)!=request.Revision)throw new JigException(409,"다른 사용자가 변경했습니다. 다시 조회하세요.");
            if(old?.ApprovalStatus=="APPROVED"&&!actor.IsMaster)throw new JigException(403,"결재 완료 기록의 수정은 마스터 관리자만 가능합니다.");
            string name=catalog.Parts.FirstOrDefault(x=>x.Part==part)?.Name
                ??production.Parts.FirstOrDefault(x=>x.Part==part)?.Name??old?.PartName??"";
            long id=old?.Id??0;string now=DateTimeOffset.Now.ToString("O");
            if(old is null)
            {
                await using var insert=conn.CreateCommand();insert.Transaction=tx;
                insert.CommandText="INSERT INTO jig_part_inspections(date,part,revision,document) VALUES($date,$part,0,'{}'); SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("$date",request.Date);insert.Parameters.AddWithValue("$part",part);
                id=Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
            }
            var ordered=JigRules.Checks.Select(d=>checks.First(x=>x.Code==d.Code) with{Note=(checks.First(x=>x.Code==d.Code).Note??"").Trim()}).ToArray();
            var record=new JigRecord(id,request.Date,part,name,inspector.AdminId,inspector.DisplayName,
                ordered.Any(x=>x.Result=="NG")?"NG":"OK","PENDING",request.Revision+1,ordered,notes,old?.CreatedAt??now,now,actor.Id,actor.DisplayName,[]);
            // 승인된 내용을 변경하면 새 revision으로 결재를 다시 받는다. 이전 결재·내용은 이력에 그대로 남는다.
            await Persist(conn,tx,record,old is null?"CREATE":"UPDATE",actor,ct);await tx.CommitAsync(ct);return record;
        }
        finally{gate.Release();}
    }
    public async Task<JigRecord> Approve(string date,string part,string stage,ApprovalRequest request,AdminSession actor,CancellationToken ct=default)
    {
        JigRules.Date(date);part=JigRules.Part(part);stage=stage.ToUpperInvariant();
        if(stage is not("STAFF" or "MANAGER"))throw new JigException(400,"결재 단계를 확인하세요.");
        if(actor.RoleCode is not("MASTER" or "ADMIN"))throw new JigException(403,"결재는 관리자만 가능합니다.");
        if(stage=="MANAGER"&&!actor.IsMaster)throw new JigException(403,"책임자 결재는 마스터·슈퍼관리자만 가능합니다.");
        if((request.Note??"").Length>1000)throw new JigException(400,"결재 메모는 1,000자 이내로 입력하세요.");
        await gate.WaitAsync(ct);
        try
        {
            await using var conn=new SqliteConnection(connectionString);await conn.OpenAsync(ct);
            await using var tx=(SqliteTransaction)await conn.BeginTransactionAsync(ct);
            var old=await Find(conn,tx,date,part,ct)??throw new JigException(404,"점검 기록이 없습니다.");
            if(old.Revision!=request.Revision)throw new JigException(409,"기록이 변경되었습니다. 다시 조회하세요.");
            if(old.Approvals.Any(x=>x.Stage==stage))throw new JigException(409,"이미 결재된 단계입니다.");
            var staff=old.Approvals.FirstOrDefault(x=>x.Stage=="STAFF");
            if(stage=="MANAGER"&&(staff is null||staff.AdminId==actor.Id))throw new JigException(400,"담당자 결재 후 다른 책임자가 결재해야 합니다.");
            string now=DateTimeOffset.Now.ToString("O");
            var record=old with{Revision=old.Revision+1,UpdatedAt=now,ApprovalStatus=stage=="STAFF"?"STAFF_APPROVED":"APPROVED",
                Approvals=old.Approvals.Append(new(stage,actor.Id,actor.DisplayName,now,(request.Note??"").Trim())).ToArray()};
            await Persist(conn,tx,record,"APPROVE_"+stage,actor,ct);await tx.CommitAsync(ct);return record;
        }
        finally{gate.Release();}
    }
    static async Task Persist(SqliteConnection conn,SqliteTransaction tx,JigRecord record,string action,AdminSession actor,CancellationToken ct)
    {
        string json=JsonSerializer.Serialize(record,Json);await using var cmd=conn.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="UPDATE jig_part_inspections SET revision=$rev,document=$doc WHERE id=$id; INSERT INTO jig_part_history(inspection_id,revision,action,at,actor_id,actor,document) VALUES($id,$rev,$action,$at,$actorId,$actor,$doc);";
        cmd.Parameters.AddWithValue("$id",record.Id);cmd.Parameters.AddWithValue("$rev",record.Revision);cmd.Parameters.AddWithValue("$doc",json);
        cmd.Parameters.AddWithValue("$action",action);cmd.Parameters.AddWithValue("$at",record.UpdatedAt);cmd.Parameters.AddWithValue("$actorId",actor.Id);cmd.Parameters.AddWithValue("$actor",actor.DisplayName);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
