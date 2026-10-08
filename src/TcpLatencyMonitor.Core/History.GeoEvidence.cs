using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TcpLatencyMonitor.Core;

public sealed partial class History
{
    public string MetadataPreference {get;set;}="ipwho.is";
    public bool RefreshCombinedMetadata(string ip)=>TryWriteOptional(db=>RebuildMetadata(db,CidrBlock.Normalize(ip)));
    public void ResetProviderFailures(string provider)
    {
        Write(db=>
        {
            using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM metadata_state WHERE name=$name; DELETE FROM geo_observation WHERE provider=$provider AND success_json IS NULL; UPDATE geo_observation SET attempt_json=success_json WHERE provider=$provider AND success_json IS NOT NULL";
            cmd.Parameters.AddWithValue("$name",provider=="ipwho.is"?"cooldown":"cooldown:"+provider);cmd.Parameters.AddWithValue("$provider",provider);cmd.ExecuteNonQuery();
        });
    }
    private static void InitializeGeoEvidence(SqliteConnection db)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";
        if((long)cmd.ExecuteScalar()!>=5)return;
        using var tx=db.BeginTransaction();cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE geo_observation(provider TEXT NOT NULL,address TEXT NOT NULL,expires_ms INTEGER NOT NULL,success_json TEXT,attempt_json TEXT NOT NULL,PRIMARY KEY(provider,address));
            CREATE INDEX ix_geo_observation_expiry ON geo_observation(expires_ms);
            INSERT INTO geo_observation SELECT 'ipwho.is',address,expires_ms,CASE WHEN json_extract(json,'$.Success')=1 THEN json END,json FROM node_metadata;
            CREATE TABLE geo_request(provider TEXT NOT NULL,time_ms INTEGER NOT NULL);
            CREATE INDEX ix_geo_request_time ON geo_request(time_ms);
            INSERT INTO geo_request SELECT 'ipwho.is',time_ms FROM metadata_request;
            CREATE TABLE node_calibration(address TEXT PRIMARY KEY,json TEXT NOT NULL);
            CREATE TABLE calibration_audit(id INTEGER PRIMARY KEY,address TEXT NOT NULL,time_ms INTEGER NOT NULL,json TEXT);
            CREATE TABLE nexttrace_import(id TEXT PRIMARY KEY,time_ms INTEGER NOT NULL,json TEXT NOT NULL);
            PRAGMA user_version=5;
            """;
        cmd.ExecuteNonQuery();tx.Commit();
    }
    private static void SaveObservation(SqliteConnection db,NodeMetadata data)
    {
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            INSERT INTO geo_observation VALUES($provider,$ip,$expiry,$success,$attempt)
            ON CONFLICT(provider,address) DO UPDATE SET
              expires_ms=CASE WHEN $success IS NOT NULL THEN $expiry ELSE MAX(expires_ms,$expiry) END,
              success_json=COALESCE($success,success_json),attempt_json=$attempt
            """;
        string json=JsonSerializer.Serialize(data with{Sources=[],Calibration=null},DataJson.Default.NodeMetadata);
        cmd.Parameters.AddWithValue("$provider",data.Source);cmd.Parameters.AddWithValue("$ip",CidrBlock.Normalize(data.Address));
        cmd.Parameters.AddWithValue("$expiry",data.Expires.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$success",data.Success?json:DBNull.Value);
        cmd.Parameters.AddWithValue("$attempt",json);cmd.ExecuteNonQuery();
    }
    public NodeMetadata? LoadProviderMetadata(string address,string provider,bool attempt=false)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT "+(attempt?"attempt_json":"COALESCE(success_json,attempt_json)")+" FROM geo_observation WHERE address=$ip AND provider=$provider";
        cmd.Parameters.AddWithValue("$ip",CidrBlock.Normalize(address));cmd.Parameters.AddWithValue("$provider",provider);
        return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize(json,DataJson.Default.NodeMetadata):null;
    }
    private static void ValidateMetadataEvidence(NodeMetadata? data)
    {
        if(data is null||!System.Net.IPAddress.TryParse(data.Address,out _)||data.Isp is null||data.Organization is null||
            data.Country is null||data.Region is null||data.City is null||data.Message is null)
            throw new JsonException("节点定位证据缺少有效地址或必需字段。");
    }
    private static NodeMetadata ReadMetadataEvidence(string json)
    {
        var data=JsonSerializer.Deserialize(json,DataJson.Default.NodeMetadata);
        ValidateMetadataEvidence(data);return data!;
    }
    private void RebuildMetadata(SqliteConnection db,string address)
    {
        address=CidrBlock.Normalize(address);var sources=new List<NodeMetadata>();
        using(var cmd=db.CreateCommand())
        {
            cmd.CommandText="SELECT success_json,attempt_json FROM geo_observation WHERE address=$ip ORDER BY provider";cmd.Parameters.AddWithValue("$ip",address);
            using var reader=cmd.ExecuteReader();while(reader.Read())
            {
                var attempt=ReadMetadataEvidence(reader.GetString(1));
                var good=reader.IsDBNull(0)?attempt:ReadMetadataEvidence(reader.GetString(0));
                sources.Add(good with{QueryState=good.QueryState+(attempt.Success?"":$" · 最近查询 {attempt.Queried.ToLocalTime():MM-dd HH:mm}：{attempt.Message}")});
            }
        }
        var calibration=ReadCalibration(db,address);
        var combined=GeoResolver.Combine(address,sources,calibration,MetadataPreference,DateTimeOffset.UtcNow)??sources.FirstOrDefault();
        if(combined is not null)SaveCombinedMetadata(db,combined);
        else{using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM node_metadata WHERE address=$ip";cmd.Parameters.AddWithValue("$ip",address);cmd.ExecuteNonQuery();}
    }
    private static NodeCalibration? ReadCalibration(SqliteConnection db,string address)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM node_calibration WHERE address=$ip";cmd.Parameters.AddWithValue("$ip",address);
        return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize(json,DataJson.Default.NodeCalibration):null;
    }
    public NodeCalibration? LoadCalibration(string address){using var db=Open();return ReadCalibration(db,CidrBlock.Normalize(address));}
    public void SetCalibration(NodeCalibration calibration)
    {
        string ip=CidrBlock.Normalize(calibration.Address);
        if(NodeMetadataClient.LocalLabel(ip) is not null||string.IsNullOrWhiteSpace(calibration.Country)||string.IsNullOrWhiteSpace(calibration.Note)||
            calibration.Country.Length>160||calibration.Region.Length>160||calibration.City.Length>160||calibration.Note.Length>1000||
            calibration.Expires<=DateTimeOffset.UtcNow||calibration.Expires> DateTimeOffset.UtcNow.AddDays(366))throw new ArgumentException("校准需公网 IP、国家和依据；复核期限须在 1 年以内。");
        calibration=calibration with{Address=ip};string json=JsonSerializer.Serialize(calibration,DataJson.Default.NodeCalibration);
        Write(db=>
        {
            using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO node_calibration VALUES($ip,$json) ON CONFLICT(address) DO UPDATE SET json=$json; INSERT INTO calibration_audit(address,time_ms,json) VALUES($ip,$time,$json)";
            cmd.Parameters.AddWithValue("$ip",ip);cmd.Parameters.AddWithValue("$json",json);cmd.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();RebuildMetadata(db,ip);
        });
    }
    public void RemoveCalibration(string address)
    {
        string ip=CidrBlock.Normalize(address);Write(db=>
        {
            using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM node_calibration WHERE address=$ip; INSERT INTO calibration_audit(address,time_ms,json) VALUES($ip,$time,NULL)";
            cmd.Parameters.AddWithValue("$ip",ip);cmd.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();RebuildMetadata(db,ip);
        });
    }
    public RouteAnnotation? ReinterpretWithCurrentEvidence(RouteRun route)
    {
        var metadata=new Dictionary<string,NodeMetadata>();
        foreach(string ip in route.Probes.Where(p=>p.Address is not null).Select(p=>CidrBlock.Normalize(p.Address!)).Distinct())
        {
            RefreshCombinedMetadata(ip);
            if(LoadNodeMetadata(ip) is {} data)metadata[ip]=data;
        }
        return SaveRouteAnnotation(RouteClassifier.Classify(route,metadata,DateTimeOffset.UtcNow,"当前证据重新解释 · 原始探测与最早注释保留"));
    }
    public void SaveNextTraceImport(NextTraceImport import)
    {
        Write(db=>{using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO nexttrace_import VALUES($id,$time,$json)";
            cmd.Parameters.AddWithValue("$id",import.Id);cmd.Parameters.AddWithValue("$time",import.Imported.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(import,DataJson.Default.NextTraceImport));cmd.ExecuteNonQuery();});
    }
    public List<NextTraceImport> LoadNextTraceImports()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM nexttrace_import ORDER BY time_ms DESC LIMIT 100";
        using var reader=cmd.ExecuteReader();var result=new List<NextTraceImport>();while(reader.Read())result.Add(JsonSerializer.Deserialize(reader.GetString(0),DataJson.Default.NextTraceImport)!);return result;
    }
}

