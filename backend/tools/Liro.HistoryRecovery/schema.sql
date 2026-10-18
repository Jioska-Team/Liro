-- Instalación aditiva e idempotente. No altera tablas ni trabajadores actuales.
CREATE TABLE IF NOT EXISTS "HistoryRecoveryControl" (
 "Id" boolean PRIMARY KEY DEFAULT true CHECK ("Id"),
 "AllowRemoteDownloads" boolean NOT NULL DEFAULT false,
 "PermissionReference" text NOT NULL DEFAULT '',
 "NextDownloadAtUtc" timestamptz NOT NULL DEFAULT NOW()
);
INSERT INTO "HistoryRecoveryControl" ("Id") VALUES (true) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS "HistoryRecoveryJobs" (
 "ItemId" uuid PRIMARY KEY REFERENCES "Items"("Id") ON DELETE CASCADE,
 "Status" text NOT NULL DEFAULT 'Pendiente',
 "Attempts" int NOT NULL DEFAULT 0,
 "NextAttemptAtUtc" timestamptz NOT NULL DEFAULT NOW(),
 "CompletedAtUtc" timestamptz,
 "SourceUrl" text,
 "Sha256" text,
 "FirstDateUtc" timestamptz,
 "LastDateUtc" timestamptz,
 "SourceDays" int NOT NULL DEFAULT 0,
 "ConflictPoints" int NOT NULL DEFAULT 0,
 "LastError" text
);
CREATE TABLE IF NOT EXISTS "MarketHistoryArchivePoints" (
 "ItemId" uuid NOT NULL REFERENCES "Items"("Id") ON DELETE CASCADE,
 "Metric" varchar(30) NOT NULL CHECK ("Metric" IN ('PrecioMedioVenta','VolumenVentas')),
 "PeriodUtc" timestamptz NOT NULL,
 "Value" numeric(28,6) NOT NULL CHECK ("Value">=0),
 "Source" varchar(40) NOT NULL DEFAULT 'rolimons' CHECK ("Source"='rolimons'),
 "ObservedAtUtc" timestamptz NOT NULL,
 PRIMARY KEY ("ItemId","Metric","PeriodUtc")
);
CREATE TABLE IF NOT EXISTS "InternetArchiveControl" (
 "Id" boolean PRIMARY KEY DEFAULT true CHECK ("Id"),
 "Enabled" boolean NOT NULL DEFAULT false,
 "NextRequestAtUtc" timestamptz NOT NULL DEFAULT NOW(),
 "LastRequestAtUtc" timestamptz,
 "LastError" text
);
INSERT INTO "InternetArchiveControl" ("Id") VALUES (true) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS "InternetArchiveJobs" (
 "ItemId" uuid PRIMARY KEY REFERENCES "Items"("Id") ON DELETE CASCADE,
 "RouteIndex" int NOT NULL DEFAULT 0,
 "ResumeKey" text,
 "IndexCompleted" boolean NOT NULL DEFAULT false,
 "Status" text NOT NULL DEFAULT 'Pendiente',
 "Attempts" int NOT NULL DEFAULT 0,
 "NextAttemptAtUtc" timestamptz NOT NULL DEFAULT NOW(),
 "LastAttemptAtUtc" timestamptz,
 "LastError" text
);
CREATE TABLE IF NOT EXISTS "InternetArchiveCaptures" (
 "ItemId" uuid NOT NULL REFERENCES "Items"("Id") ON DELETE CASCADE,
 "ReplayUrl" text NOT NULL,
 "OriginalUrl" text NOT NULL,
 "CapturedAtUtc" timestamptz NOT NULL,
 "Digest" text,
 "Sha256" text,
 "Status" text NOT NULL DEFAULT 'Pendiente',
 "Attempts" int NOT NULL DEFAULT 0,
 "NextAttemptAtUtc" timestamptz NOT NULL DEFAULT NOW(),
 "ProcessedAtUtc" timestamptz,
 "Points" int NOT NULL DEFAULT 0,
 "Conflicts" int NOT NULL DEFAULT 0,
 "LastError" text,
 PRIMARY KEY ("ItemId","ReplayUrl")
);
CREATE TABLE IF NOT EXISTS "MarketHistoryInternetArchivePoints" (
 "ItemId" uuid NOT NULL REFERENCES "Items"("Id") ON DELETE CASCADE,
 "Metric" varchar(30) NOT NULL CHECK ("Metric" IN ('PrecioMedioVenta','VolumenVentas')),
 "PeriodUtc" timestamptz NOT NULL,
 "OriginalPeriodUtc" timestamptz NOT NULL,
 "Value" numeric(28,6) NOT NULL CHECK ("Value">=0),
 "Source" varchar(40) NOT NULL DEFAULT 'roblox-internet-archive',
 "ObservedAtUtc" timestamptz NOT NULL,
 "CapturedAtUtc" timestamptz NOT NULL,
 "ReplayUrl" text NOT NULL,
 "Sha256" text NOT NULL,
 PRIMARY KEY ("ItemId","Metric","PeriodUtc")
);
-- Un punto por artículo/métrica/día UTC. Roblox siempre tiene prioridad,
-- incluso si mercado guarda el mismo día DESPUÉS de la recuperación.
CREATE OR REPLACE VIEW "MarketHistoryCombined" AS
SELECT DISTINCT ON (h."ItemId",h."Metric",(h."PeriodUtc" AT TIME ZONE 'UTC')::date)
 h."ItemId",h."Source",h."Metric",((h."PeriodUtc" AT TIME ZONE 'UTC')::date::timestamp AT TIME ZONE 'UTC') AS "PeriodUtc",h."Value",h."ObservedAtUtc"
FROM (
 SELECT "ItemId","Source","Metric","PeriodUtc","Value","ObservedAtUtc",0 AS origin
 FROM "MarketHistoryPoints"
 UNION ALL
 SELECT "ItemId","Source","Metric","PeriodUtc","Value","ObservedAtUtc",1 AS origin
 FROM "MarketHistoryArchivePoints"
 UNION ALL
 SELECT "ItemId","Source","Metric","PeriodUtc","Value","ObservedAtUtc",2 AS origin
 FROM "MarketHistoryInternetArchivePoints"
) h
ORDER BY h."ItemId",h."Metric",(h."PeriodUtc" AT TIME ZONE 'UTC')::date,
 CASE WHEN h."Source"='marketplace-sales' THEN 0 ELSE 1 END,
 h.origin,h."PeriodUtc" DESC,h."ObservedAtUtc" DESC,h."Source";
