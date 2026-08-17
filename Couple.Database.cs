using System;
using System.Threading.Tasks;
using MySqlConnector;

namespace Ins.Couple;

public sealed partial class Couple
{
    private MySqlConnection GetConnection()
    {
        return new MySqlConnection(_connectionString);
    }

    private async Task CreateDatabaseConnection(string connectionString)
    {
        _connectionString = connectionString ?? string.Empty;

        try
        {
            await using MySqlConnection connection = GetConnection();
            await connection.OpenAsync();
            await CreateTable(connection);
            _isDbConnected = true;
            Echo("数据库已连接成功");
        }
        catch (Exception ex)
        {
            _isDbConnected = false;
            _connectionString = string.Empty;
            EchoWarning($"Unable to connect to the database: {ex.Message}");
        }
    }

    private async Task CreateTable(MySqlConnection connection)
    {
        try
        {
            await using var cmd = new MySqlCommand(
                @"CREATE TABLE IF NOT EXISTS `social_couple` (
                `id` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY COMMENT '记录每一次组CP的ID',
                `steamid_0` BIGINT UNSIGNED NOT NULL COMMENT '女方',
                `steamid_1` BIGINT UNSIGNED NOT NULL COMMENT '男方',
                `created_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '登记时间',
                `canceled_at` TIMESTAMP NULL DEFAULT NULL COMMENT '分手时间,未分手时为空',
                `status` TINYINT(1) NOT NULL DEFAULT 1 COMMENT '1: 热恋中; 0: 分手',
                `id0_lastseen` TIMESTAMP NULL DEFAULT NULL COMMENT '女方上次游玩时间',
                `id1_lastseen` TIMESTAMP NULL DEFAULT NULL COMMENT '男方上次游玩时间',
                `cooldown_until` TIMESTAMP NULL DEFAULT NULL COMMENT '冷静期结束时间'
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;", connection);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            EchoWarning($"There was an error when creating database: {ex.Message}");
        }
    }

    private void TryReconnectDatabase()
    {
        if (_isDbConnected)
        {
            return;
        }

        if (!TryGetConnectionString(out string connectionString, out string errorMessage))
        {
            _isDbConnected = false;
            EchoWarning(errorMessage);
            return;
        }

        _ = CreateDatabaseConnection(connectionString);
    }

    private bool TryGetConnectionString(out string connectionString, out string errorMessage)
    {
        string? configured =
            _configuration["Couple:ConnectionString"]
            ?? _configuration["ConnectionStrings:Couple"]
            ?? _configuration["ConnectionStrings:KepCore"]
            ?? _configuration["KepCore:ConnectionString"]
            ?? _configuration["KepCore:Database:ConnectionString"]
            ?? Environment.GetEnvironmentVariable("COUPLE_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("KEPCORE_CONNECTION_STRING");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            connectionString = configured;
            errorMessage = string.Empty;
            return true;
        }

        connectionString = string.Empty;
        errorMessage = "未找到数据库连接字符串，请在配置中设置 Couple:ConnectionString / ConnectionStrings:Couple，或设置环境变量 COUPLE_CONNECTION_STRING。";
        return false;
    }

    private async Task<bool> AddCoupleAsync(ulong steamId0, ulong steamId1)
    {
        if (!CanPersistPlayer(steamId0) || !CanPersistPlayer(steamId1))
        {
            return false;
        }

        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();

            const string sql = @"
                INSERT INTO `social_couple` (`steamid_0`, `steamid_1`, `created_at`, `status`)
                SELECT @steamId0, @steamId1, CURRENT_TIMESTAMP, 1
                FROM DUAL
                WHERE EXISTS (
                    SELECT 1 FROM `player_playtime` WHERE `steamid` = @steamId0Text
                )
                  AND EXISTS (
                    SELECT 1 FROM `player_playtime` WHERE `steamid` = @steamId1Text
                );";
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@steamId0", steamId0);
            cmd.Parameters.AddWithValue("@steamId1", steamId1);
            cmd.Parameters.AddWithValue("@steamId0Text", steamId0.ToString());
            cmd.Parameters.AddWithValue("@steamId1Text", steamId1.ToString());
            int rowsAffected = await cmd.ExecuteNonQueryAsync();
            if (rowsAffected == 0)
            {
                EchoWarning("No eligible players found to add a couple.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            EchoWarning($"Error when adding a couple: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> BreakUpCoupleAsync(ulong steamId0, ulong steamId1)
    {
        if (!CanPersistPlayer(steamId0) || !CanPersistPlayer(steamId1))
        {
            return false;
        }

        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();

            const string sql = @"UPDATE `social_couple`
                           SET `status` = 0,
                           `canceled_at` = CURRENT_TIMESTAMP,
                           `cooldown_until` = DATE_ADD(CURRENT_TIMESTAMP, INTERVAL 3 DAY)
                           WHERE (`steamid_0` = @steamId0 AND `steamid_1` = @steamId1 OR `steamid_0` = @steamId1 AND `steamid_1` = @steamId0)
                           AND `status` = 1
                           AND EXISTS (
                               SELECT 1 FROM `player_playtime` WHERE `steamid` = @steamId0Text
                           )
                           AND EXISTS (
                               SELECT 1 FROM `player_playtime` WHERE `steamid` = @steamId1Text
                           );";
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@steamId0", steamId0);
            cmd.Parameters.AddWithValue("@steamId1", steamId1);
            cmd.Parameters.AddWithValue("@steamId0Text", steamId0.ToString());
            cmd.Parameters.AddWithValue("@steamId1Text", steamId1.ToString());

            int rowsAffected = await cmd.ExecuteNonQueryAsync();
            if (rowsAffected == 0)
            {
                EchoWarning("No active relationship found to break up.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            EchoWarning($"Error when breaking up a couple: {ex.Message}");
            return false;
        }
    }

    private async Task UpdateLastSeenAsync(ulong steamId, CPSide side)
    {
        if (!CanPersistPlayer(steamId))
        {
            return;
        }

        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();

            string sql = side == CPSide.Female
                ? @"UPDATE `social_couple`
                       SET `id0_lastseen` = CURRENT_TIMESTAMP
                       WHERE (`steamid_0` = @steamId OR `steamid_1` = @steamId)
                       AND `status` = 1
                       AND EXISTS (
                           SELECT 1 FROM `player_playtime` WHERE `steamid` = CAST(`social_couple`.`steamid_0` AS CHAR)
                       )
                       AND EXISTS (
                           SELECT 1 FROM `player_playtime` WHERE `steamid` = CAST(`social_couple`.`steamid_1` AS CHAR)
                       );"
                : @"UPDATE `social_couple`
                       SET `id1_lastseen` = CURRENT_TIMESTAMP
                       WHERE (`steamid_0` = @steamId OR `steamid_1` = @steamId)
                       AND `status` = 1
                       AND EXISTS (
                           SELECT 1 FROM `player_playtime` WHERE `steamid` = CAST(`social_couple`.`steamid_0` AS CHAR)
                       )
                       AND EXISTS (
                           SELECT 1 FROM `player_playtime` WHERE `steamid` = CAST(`social_couple`.`steamid_1` AS CHAR)
                       );";

            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@steamId", steamId);

            int rowsAffected = await cmd.ExecuteNonQueryAsync();
            if (rowsAffected == 0)
            {
                EchoWarning("No active relationship found to update last seen time.");
            }
        }
        catch (Exception ex)
        {
            EchoWarning($"Error when updating last seen time: {ex.Message}");
        }
    }

    private async Task<DateTime?> GetLastSeenAsync(ulong steamId, CPSide side)
    {
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();

            string columnToQuery = side == CPSide.Female ? "id0_lastseen" : "id1_lastseen";
            string sql = $@"
                SELECT `{columnToQuery}`
                FROM `social_couple`
                WHERE (`steamid_0` = @steamId OR `steamid_1` = @steamId)
                AND `status` = 1
                AND EXISTS (
                    SELECT 1 FROM `player_playtime` WHERE `steamid` = CAST(`social_couple`.`steamid_0` AS CHAR)
                )
                AND EXISTS (
                    SELECT 1 FROM `player_playtime` WHERE `steamid` = CAST(`social_couple`.`steamid_1` AS CHAR)
                )
                LIMIT 1;";

            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@steamId", steamId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return reader.IsDBNull(0) ? null : reader.GetDateTime(0);
            }

            return null;
        }
        catch (Exception ex)
        {
            EchoWarning($"Error when retrieving last seen time: {ex.Message}");
            return null;
        }
    }

    private async Task<(ulong? spouseSteamId, string? spouseGender)> GetSpouseSteamIdAndGenderAsync(ulong steamId)
    {
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();

            const string sql = @"
                SELECT
                    CASE
                        WHEN `steamid_0` = @steamId THEN `steamid_1`
                        WHEN `steamid_1` = @steamId THEN `steamid_0`
                    END AS `spouseSteamId`,
                    CASE
                        WHEN `steamid_0` = @steamId THEN '老公'
                        WHEN `steamid_1` = @steamId THEN '老婆'
                    END AS `spouseGender`
                FROM `social_couple`
                WHERE (`steamid_0` = @steamId OR `steamid_1` = @steamId)
                AND `status` = 1
                AND EXISTS (
                    SELECT 1 FROM `player_playtime` WHERE `steamid` = CAST(`social_couple`.`steamid_0` AS CHAR)
                )
                AND EXISTS (
                    SELECT 1 FROM `player_playtime` WHERE `steamid` = CAST(`social_couple`.`steamid_1` AS CHAR)
                );";

            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@steamId", steamId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                ulong? spouseSteamId = reader.IsDBNull(0) ? null : reader.GetUInt64("spouseSteamId");
                string? spouseGender = reader.IsDBNull(1) ? null : reader.GetString("spouseGender");
                return (spouseSteamId, spouseGender);
            }

            return (null, null);
        }
        catch (Exception ex)
        {
            EchoWarning($"Error when retrieving spouse's steamid and gender: {ex.Message}");
            return (null, null);
        }
    }

    private async Task<bool> CanMarryAgainAsync(ulong steamId)
    {
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();

            const string sql = @"
                SELECT `cooldown_until`
                FROM `social_couple`
                WHERE (`steamid_0` = @steamId OR `steamid_1` = @steamId)
                  AND `status` = 0
                  AND EXISTS (
                      SELECT 1 FROM `player_playtime` WHERE `steamid` = CAST(`social_couple`.`steamid_0` AS CHAR)
                  )
                  AND EXISTS (
                      SELECT 1 FROM `player_playtime` WHERE `steamid` = CAST(`social_couple`.`steamid_1` AS CHAR)
                  )
                ORDER BY `canceled_at` DESC
                LIMIT 1";

            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@steamId", steamId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                DateTime? cooldownUntil = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
                return cooldownUntil == null || cooldownUntil <= DateTime.UtcNow;
            }

            return true;
        }
        catch (Exception ex)
        {
            EchoWarning($"Error when checking cooldown: {ex.Message}");
            return false;
        }
    }
}
