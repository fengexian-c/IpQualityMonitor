using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TcpLatencyMonitor.Core;

public sealed partial class History
{
    private static void InitializeMetadata(SqliteConnection db)
    {
        using var version=db.CreateCommand();version.CommandText="PRAGMA user_version";
        if((long)version.ExecuteScalar()!>=3)return;
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE node_metadata(address TEXT PRIMARY KEY,expires_ms INTEGER NOT NULL,json TEXT NOT NULL);
            CREATE TABLE metadata_request(time_ms INTEGER NOT NULL);
            CREATE INDEX ix_metadata_request_time ON metadata_request(time_ms);
            CREATE TABLE metadata_state(name TEXT PRIMARY KEY,value INTEGER NOT NULL);
            PRAGMA user_version=3;
            """;cmd.ExecuteNonQuery();tx.Commit();
    }
    public NodeMetadata? LoadNodeMetadata(string address)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM node_metadata WHERE address=$ip";
        cmd.Parameters.AddWithValue("$ip",address);
        return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize(json,DataJson.Default.NodeMetadata):null;
    }
    public void SaveNodeMetadata(NodeMetadata data)
    {
        Write(db=>
        {
        SaveObservation(db,data);
        RebuildMetadata(db,data.Address);
        });
    }
    private static void SaveCombinedMetadata(SqliteConnection db,NodeMetadata data)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO node_metadata VALUES($ip,$expiry,$json) ON CONFLICT(address) DO UPDATE SET expires_ms=excluded.expires_ms,json=excluded.json";
        cmd.Parameters.AddWithValue("$ip",data.Address);cmd.Parameters.AddWithValue("$expiry",Math.Max(data.Expires.ToUnixTimeMilliseconds(),data.Calibration?.Expires.ToUnixTimeMilliseconds()??0));
        cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(data,DataJson.Default.NodeMetadata));cmd.ExecuteNonQuery();
    }
    public DateTimeOffset MetadataCooldown(string provider="ipwho.is")
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM metadata_state WHERE name=$name";cmd.Parameters.AddWithValue("$name",provider=="ipwho.is"?"cooldown":"cooldown:"+provider);
        return DateTimeOffset.FromUnixTimeMilliseconds(cmd.ExecuteScalar() is long n?n:0);
    }
    public void SetMetadataCooldown(DateTimeOffset until,string provider="ipwho.is")
    {
        Write(db=>
        {
        using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO metadata_state VALUES($name,$v) ON CONFLICT(name) DO UPDATE SET value=MAX(value,excluded.value)";cmd.Parameters.AddWithValue("$name",provider=="ipwho.is"?"cooldown":"cooldown:"+provider);
        cmd.Parameters.AddWithValue("$v",until.ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();
            });
    }
    public bool ReserveMetadataRequest(DateTimeOffset now,string provider="ipwho.is")
    {
        bool reserved=false;
        Write(db=>
        {
            using var cmd=db.CreateCommand();
            cmd.CommandText="DELETE FROM geo_request WHERE time_ms <= $old; SELECT COUNT(*) FROM geo_request WHERE provider=$provider";
            cmd.Parameters.AddWithValue("$provider",provider);
            cmd.Parameters.AddWithValue("$old",now.AddDays(-1).ToUnixTimeMilliseconds());
            if((long)cmd.ExecuteScalar()!>=900)return;
            cmd.CommandText="INSERT INTO geo_request VALUES($provider,$now)";cmd.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();reserved=true;
        });
        return reserved;
    }
}
