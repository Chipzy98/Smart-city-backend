/* ============================================================================
   Smart City backend — DB migration 001
   Traffic ML dataset columns, prediction accuracy table, ML run history,
   performance indexes and demo users.

   Safe to run more than once (every step checks before it changes anything).
   Run in SSMS against the database used in "DefaultConnection".
   ============================================================================ */

SET NOCOUNT ON;

/* ---------------------------------------------------------------------------
   1. smart_TrafficData — extra columns for the ML dataset (all NULLable)
   --------------------------------------------------------------------------- */
IF COL_LENGTH('dbo.smart_TrafficData', 'LocationName')     IS NULL ALTER TABLE dbo.smart_TrafficData ADD LocationName     NVARCHAR(200) NULL;
IF COL_LENGTH('dbo.smart_TrafficData', 'CurrentSpeedKmh')  IS NULL ALTER TABLE dbo.smart_TrafficData ADD CurrentSpeedKmh  REAL NULL;
IF COL_LENGTH('dbo.smart_TrafficData', 'FreeFlowSpeedKmh') IS NULL ALTER TABLE dbo.smart_TrafficData ADD FreeFlowSpeedKmh REAL NULL;
IF COL_LENGTH('dbo.smart_TrafficData', 'Confidence')       IS NULL ALTER TABLE dbo.smart_TrafficData ADD Confidence       REAL NULL;
IF COL_LENGTH('dbo.smart_TrafficData', 'Temperature')      IS NULL ALTER TABLE dbo.smart_TrafficData ADD Temperature      REAL NULL;
IF COL_LENGTH('dbo.smart_TrafficData', 'PrecipitationMm')  IS NULL ALTER TABLE dbo.smart_TrafficData ADD PrecipitationMm  REAL NULL;
IF COL_LENGTH('dbo.smart_TrafficData', 'Weather')          IS NULL ALTER TABLE dbo.smart_TrafficData ADD Weather          INT NULL;
IF COL_LENGTH('dbo.smart_TrafficData', 'LocalHour')        IS NULL ALTER TABLE dbo.smart_TrafficData ADD LocalHour        INT NULL;
IF COL_LENGTH('dbo.smart_TrafficData', 'LocalDayOfWeek')   IS NULL ALTER TABLE dbo.smart_TrafficData ADD LocalDayOfWeek   INT NULL;
IF COL_LENGTH('dbo.smart_TrafficData', 'Source')           IS NULL ALTER TABLE dbo.smart_TrafficData ADD Source           NVARCHAR(20) NULL;
GO

/* Coordinates need at least 4 decimal places (6 recommended).
   If the original columns were created as DECIMAL(18,2), widen the scale. */
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.smart_TrafficData') AND name = 'Latitude' AND scale < 6)
BEGIN
    ALTER TABLE dbo.smart_TrafficData ALTER COLUMN Latitude  DECIMAL(9,6) NOT NULL;
    ALTER TABLE dbo.smart_TrafficData ALTER COLUMN Longitude DECIMAL(9,6) NOT NULL;
    PRINT 'smart_TrafficData Latitude/Longitude widened to DECIMAL(9,6).';
END
GO

/* Mark older rows so the UI can show where data came from */
UPDATE dbo.smart_TrafficData SET Source = 'legacy' WHERE Source IS NULL;
GO

/* ---------------------------------------------------------------------------
   2. smart_TrafficPrediction — stored forecasts, compared with live data later
   --------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.smart_TrafficPrediction', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.smart_TrafficPrediction
    (
        PredictionId        UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_smart_TrafficPrediction PRIMARY KEY,
        UserId              UNIQUEIDENTIFIER NOT NULL,
        LocationName        NVARCHAR(200)    NOT NULL,
        Latitude            DECIMAL(9,6)     NOT NULL,
        Longitude           DECIMAL(9,6)     NOT NULL,
        TargetTimeUtc       DATETIME2        NOT NULL,
        PredictedCongestion REAL             NOT NULL,
        ActualCongestion    REAL             NULL,
        ModelUsed           NVARCHAR(20)     NOT NULL,
        Weather             INT              NOT NULL,
        Temperature         REAL             NULL,
        IsActive            BIT              NOT NULL CONSTRAINT DF_smart_TrafficPrediction_IsActive DEFAULT (1),
        CreatedDateTime     DATETIME2        NOT NULL
    );
    PRINT 'Created smart_TrafficPrediction.';
END
GO

/* ---------------------------------------------------------------------------
   3. smart_MLModelRun — training history with evaluation metrics
   --------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.smart_MLModelRun', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.smart_MLModelRun
    (
        ModelRunId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_smart_MLModelRun PRIMARY KEY,
        ModelName            NVARCHAR(100)    NOT NULL,
        Algorithm            NVARCHAR(100)    NOT NULL,
        [RowCount]           INT              NOT NULL,
        TrainRows            INT              NOT NULL,
        TestRows             INT              NOT NULL,
        MeanAbsoluteError    FLOAT            NOT NULL,
        RootMeanSquaredError FLOAT            NOT NULL,
        RSquared             FLOAT            NOT NULL,
        TrainedAt            DATETIME2        NOT NULL,
        TrainedByUserId      UNIQUEIDENTIFIER NOT NULL,
        IsActive             BIT              NOT NULL CONSTRAINT DF_smart_MLModelRun_IsActive DEFAULT (1)
    );
    PRINT 'Created smart_MLModelRun.';
END
GO

/* ---------------------------------------------------------------------------
   4. Indexes for the new queries
   --------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_smart_TrafficData_RecordedAt' AND object_id = OBJECT_ID('dbo.smart_TrafficData'))
    CREATE INDEX IX_smart_TrafficData_RecordedAt ON dbo.smart_TrafficData (RecordedAt DESC) INCLUDE (CongestionLevel, Source, IsActive);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_smart_TrafficData_Location' AND object_id = OBJECT_ID('dbo.smart_TrafficData'))
    CREATE INDEX IX_smart_TrafficData_Location ON dbo.smart_TrafficData (Latitude, Longitude) INCLUDE (RecordedAt, CongestionLevel, LocalHour);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_smart_TrafficPrediction_Target' AND object_id = OBJECT_ID('dbo.smart_TrafficPrediction'))
    CREATE INDEX IX_smart_TrafficPrediction_Target ON dbo.smart_TrafficPrediction (TargetTimeUtc) INCLUDE (Latitude, Longitude, ActualCongestion);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_smart_AIInsight_Created' AND object_id = OBJECT_ID('dbo.smart_AIInsight'))
    CREATE INDEX IX_smart_AIInsight_Created ON dbo.smart_AIInsight (CreatedDateTime DESC) INCLUDE (Type, IsActive);
GO

/* ---------------------------------------------------------------------------
   5. Demo users (shown on the login page). Public registration only creates
      "user" accounts, so manager/admin accounts come from here.
      Passwords: Admin@123 / Manager@123 / User@123   (BCrypt, work factor 11)
      ⚠ Change these passwords before any real deployment.
   --------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.smart_User WHERE Email = 'admin@ecocity.com')
    INSERT INTO dbo.smart_User (UserId, Name, Email, PasswordHash, Role, IsActive, CreatedDateTime)
    VALUES (NEWID(), 'EcoCity Admin', 'admin@ecocity.com',
            '$2a$11$sS3K3iNFwUWIxw0t1TdcSOa/H3PfWSputGmZa4FqgNUirhoYD7iau', 'admin', 1, SYSUTCDATETIME());

IF NOT EXISTS (SELECT 1 FROM dbo.smart_User WHERE Email = 'manager@ecocity.com')
    INSERT INTO dbo.smart_User (UserId, Name, Email, PasswordHash, Role, IsActive, CreatedDateTime)
    VALUES (NEWID(), 'City Manager', 'manager@ecocity.com',
            '$2a$11$SNppATPw17u.9N.HzIvePOaHQTQIaveh0dpXOsEMiTpMYSYLU9.Ha', 'manager', 1, SYSUTCDATETIME());

IF NOT EXISTS (SELECT 1 FROM dbo.smart_User WHERE Email = 'user@ecocity.com')
    INSERT INTO dbo.smart_User (UserId, Name, Email, PasswordHash, Role, IsActive, CreatedDateTime)
    VALUES (NEWID(), 'Demo User', 'user@ecocity.com',
            '$2a$11$EuMW8sAxBv8JF22XPM/C4eiasD1T0kySaIucs83TMttaeeka6TdTK', 'user', 1, SYSUTCDATETIME());
GO

/* Emails are compared in lower case by the API */
UPDATE dbo.smart_User SET Email = LOWER(LTRIM(RTRIM(Email))) WHERE Email <> LOWER(LTRIM(RTRIM(Email)));
GO

PRINT 'Migration 001 complete.';
