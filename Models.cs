using System.Globalization;
using System.Text.RegularExpressions;

namespace Dnt.JigDaily;

public sealed record JigCheck(int No, string Code, string Name, string Criterion, string Method);
public static class JigRules
{
    public static readonly JigCheck[] Checks =
    [
        new(1,"CLAMP_CYLINDER","클램프 / 실린더 작동","유동 및 작동상태 확인","육안 / 작동실험"),
        new(2,"SPATTER_COVER","스패터 방지덮개","스패터 덮개 파손 없을 것","육안"),
        new(3,"AIR_LEAK","AIR 압력 / 누기","에어호스 누기 없을 것","게이지 / 촉각"),
        new(4,"FOOL_PROOF","F/PROOF 점검","정상 작동할 것","마스터샘플"),
        new(5,"BOLT_NUT","볼트 / 너트","볼트·너트 풀림 없을 것","육안 / 촉수"),
        new(6,"JIG_CLEAN","지그 청결 상태","작업 전·후 스패터, 오염, 이물질 확인","육안"),
        new(7,"BLOCK_PIN","블록 / 핀 유동","유동 및 풀림 없을 것","육안 / 촉수")
    ];
    public static DateOnly Date(string? value) => DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
        ? date : throw new JigException(400,"점검일을 확인하세요.");
    public static string Part(string? value)
    {
        var part=(value??"").Trim().ToUpperInvariant();
        if(!Regex.IsMatch(part,@"^[A-Z0-9][A-Z0-9_-]{1,79}$")) throw new JigException(400,"품번을 확인하세요.");
        return part;
    }
    public static int? Robot(string? value)
    {
        var digits=Regex.Replace(value??"",@"\D","");
        return int.TryParse(digits,out int n)&&n is >=1 and <=25?n:null;
    }
    public static string Status(JigRecord? record) => record is null?"MISSING":record.Overall=="NG"?"NG":"COMPLETE";
    public static string StatusText(string code) => code switch
    { "COMPLETE"=>"점검 완료","NG"=>"NG 조치 필요",_=>"미점검" };
}
public sealed class JigException(int status,string message):Exception(message){public int Status{get;}=status;}
public sealed record HubPart(string Part,string Name,IReadOnlyList<int> AssignedRobots);
public sealed record ProductionPart(string Date,int Robot,string Part,string Name,int Qty);
public sealed record ProductionRange(bool Available,string Message,IReadOnlyList<ProductionPart> Parts);
public sealed record Catalog(bool Available,string Message,IReadOnlyList<HubPart> Parts);
public sealed record CheckResult(string Code,string Result,string Note);
public sealed record SaveInspection(string Date,string Part,long InspectorId,int Revision,IReadOnlyList<CheckResult> Checks,string? Notes);
public sealed record ApprovalRequest(int Revision,string? Note);
public sealed record JigApproval(string Stage,long AdminId,string Name,string At,string Note);
public sealed record JigRecord(long Id,string Date,string Part,string PartName,long InspectorId,string InspectorName,
    string Overall,string ApprovalStatus,int Revision,IReadOnlyList<CheckResult> Checks,string Notes,
    string CreatedAt,string UpdatedAt,long SavedById,string SavedByName,IReadOnlyList<JigApproval> Approvals);
public sealed record JigHistory(int Revision,string Action,string At,string Actor,string Snapshot);
public sealed record JigRow(string Date,string Part,string Name,IReadOnlyList<int> AssignedRobots,bool? Produced,bool Required,
    string Status,string StatusText,string ApprovalStatus,string InspectorName,long? InspectionId,int Revision,string? ImageUrl);
public sealed record DailyBoard(string Date,bool ProductionAvailable,string ProductionMessage,bool CatalogAvailable,string CatalogMessage,
    IReadOnlyList<JigRow> Rows);
