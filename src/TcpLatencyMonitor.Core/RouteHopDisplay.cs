namespace TcpLatencyMonitor.Core;

public static class RouteHopDisplay
{
    public static string Title(int ttl,IEnumerable<HopProbe> probes)
    {
        var items=probes.ToArray();
        string text=$"第 {ttl} 跳 · 回应 {items.Count(p=>p.Address is not null)}/{items.Length} · 超时 {items.Count(p=>p.Status==11010)} 次";
        var errors=items.Where(p=>p.Address is null&&p.Status!=11010).GroupBy(p=>p.Status).ToArray();
        if(errors.Length>0)text+=" · 探测错误 "+string.Join(" / ",errors.Select(g=>$"{g.Key} × {g.Count()}"));
        var additional=items.Where(p=>p.IsSupplemental).ToArray();
        if(additional.Length>0)
        {
            var initial=items.Where(p=>!p.IsSupplemental).ToArray();
            text+=$"（初测 {initial.Count(p=>p.Address is not null)}/{initial.Length}，补测 {additional.Count(p=>p.Address is not null)}/{additional.Length}）";
        }
        return text;
    }
}
