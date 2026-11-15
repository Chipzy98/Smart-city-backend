/* ============================================================================
   Smart City backend — DB migration 002
   Fix: "Unable to cast object of type 'System.Double' to type 'System.Single'"

   The C# entities use float (= SQL REAL) for these columns, but in some
   databases the original tables created them as FLOAT (= C# double).
   EF Core then fails when it reads them (Route history, Energy, Carbon pages).

   This script changes every such column from FLOAT to REAL, keeping NULL /
   NOT NULL and any DEFAULT constraint. Columns that are already REAL are skipped.
   Safe to run more than once. Works on SQL Server 2014 and later.
   ============================================================================ */

SET NOCOUNT ON;

DECLARE @cols TABLE (TableName SYSNAME, ColumnName SYSNAME);
INSERT INTO @cols (TableName, ColumnName) VALUES
    ('smart_TrafficData',       'CongestionLevel'),
    ('smart_TrafficData',       'CurrentSpeedKmh'),
    ('smart_TrafficData',       'FreeFlowSpeedKmh'),
    ('smart_TrafficData',       'Confidence'),
    ('smart_TrafficData',       'Temperature'),
    ('smart_TrafficData',       'PrecipitationMm'),
    ('smart_RouteTraffic',      'DistanceKm'),
    ('smart_RouteTraffic',      'CongestionLevel'),
    ('smart_RouteTraffic',      'RiskScore'),
    ('smart_EnergyUsage',       'UsageUnits'),
    ('smart_EnergyUsage',       'Temperature'),
    ('smart_EnergyUsage',       'PredictedBill'),
    ('smart_WasteRecord',       'Confidence'),
    ('smart_WasteRecord',       'Co2Saved'),
    ('smart_TrafficPrediction', 'PredictedCongestion'),
    ('smart_TrafficPrediction', 'ActualCongestion'),
    ('smart_TrafficPrediction', 'Temperature');

/* smart_TrafficData.CongestionLevel is INCLUDEd in two indexes from migration 001.
   An index blocks a type change, so drop them first (re-created at the end). */
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.smart_TrafficData')
             AND name = 'CongestionLevel' AND system_type_id = 62 /* float */)
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_smart_TrafficData_RecordedAt' AND object_id = OBJECT_ID('dbo.smart_TrafficData'))
        DROP INDEX IX_smart_TrafficData_RecordedAt ON dbo.smart_TrafficData;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_smart_TrafficData_Location' AND object_id = OBJECT_ID('dbo.smart_TrafficData'))
        DROP INDEX IX_smart_TrafficData_Location ON dbo.smart_TrafficData;
END

DECLARE @table SYSNAME, @column SYSNAME, @nullable BIT,
        @dfName SYSNAME, @dfDef NVARCHAR(MAX), @sql NVARCHAR(MAX);

DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT x.TableName, x.ColumnName, sc.is_nullable
    FROM @cols x
    JOIN sys.columns sc
      ON sc.object_id = OBJECT_ID('dbo.' + x.TableName)
     AND sc.name = x.ColumnName
    WHERE sc.system_type_id = 62;   -- FLOAT only; REAL columns are already correct

OPEN c;
FETCH NEXT FROM c INTO @table, @column, @nullable;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        SET @dfName = NULL; SET @dfDef = NULL;

        -- remember and drop a DEFAULT constraint, if the column has one
        SELECT @dfName = dc.name, @dfDef = dc.definition
        FROM sys.default_constraints dc
        JOIN sys.columns sc ON sc.object_id = dc.parent_object_id AND sc.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID('dbo.' + @table) AND sc.name = @column;

        IF @dfName IS NOT NULL
        BEGIN
            SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@table) + N' DROP CONSTRAINT ' + QUOTENAME(@dfName) + N';';
            EXEC sp_executesql @sql;
        END

        SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@table) + N' ALTER COLUMN ' + QUOTENAME(@column)
                 + N' REAL ' + CASE WHEN @nullable = 1 THEN N'NULL' ELSE N'NOT NULL' END + N';';
        EXEC sp_executesql @sql;

        IF @dfName IS NOT NULL
        BEGIN
            SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@table) + N' ADD CONSTRAINT ' + QUOTENAME(@dfName)
                     + N' DEFAULT ' + @dfDef + N' FOR ' + QUOTENAME(@column) + N';';
            EXEC sp_executesql @sql;
        END

        PRINT 'Changed ' + @table + '.' + @column + ' from FLOAT to REAL.';
    END TRY
    BEGIN CATCH
        PRINT 'Could not change ' + @table + '.' + @column + ': ' + ERROR_MESSAGE();
    END CATCH

    FETCH NEXT FROM c INTO @table, @column, @nullable;
END
CLOSE c;
DEALLOCATE c;

/* Re-create the indexes from migration 001 if they were dropped above */
IF COL_LENGTH('dbo.smart_TrafficData', 'Source') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_smart_TrafficData_RecordedAt' AND object_id = OBJECT_ID('dbo.smart_TrafficData'))
    EXEC (N'CREATE INDEX IX_smart_TrafficData_RecordedAt ON dbo.smart_TrafficData (RecordedAt DESC) INCLUDE (CongestionLevel, Source, IsActive);');

IF COL_LENGTH('dbo.smart_TrafficData', 'LocalHour') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_smart_TrafficData_Location' AND object_id = OBJECT_ID('dbo.smart_TrafficData'))
    EXEC (N'CREATE INDEX IX_smart_TrafficData_Location ON dbo.smart_TrafficData (Latitude, Longitude) INCLUDE (RecordedAt, CongestionLevel, LocalHour);');

PRINT 'Migration 002 complete.';
