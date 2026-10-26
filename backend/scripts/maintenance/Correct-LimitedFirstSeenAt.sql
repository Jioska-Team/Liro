-- PostgreSQL: manual correction of FirstSeenAt for the 30 selected official Limiteds.
-- Assigns descending registration timestamps to reproduce the requested order.
-- These are chosen ordering dates, not verified Roblox conversion dates.
-- Also affects the "recently added" section, which uses FirstSeenAt.
-- Preview by default: inspect the returned old/new dates, then replace ROLLBACK with COMMIT to apply.
BEGIN;
SET LOCAL lock_timeout = '10s';
CREATE TEMP TABLE liro_limited_registration_order (
    asset_id bigint PRIMARY KEY,
    position integer UNIQUE NOT NULL
) ON COMMIT DROP;
INSERT INTO liro_limited_registration_order (asset_id, position) VALUES
    (7584199117, 1), -- Pumpkin Warrior Helmet
    (16017997575, 2), -- Manly Bee Hat
    (7850869479, 3), -- Fuzzy Fox Earmuffs
    (11386269063, 4), -- Autumn Leaf Blower Backpack
    (16491604946, 5), -- Brilliant Unicorn Horn
    (93762663362787, 6), -- Shedletsekkar
    (17682484184, 7), -- Extraterrestrial Shades
    (16491634419, 8), -- Striking White Owl Shoulder Pet
    (76292007466829, 9), -- Dark Guardian Angel
    (112243615546468, 10), -- Ice Fire Ram Skull Helm
    (17266515535, 11), -- Molten Lava Wings
    (16972690019, 12), -- Atomic Blue Hairdo
    (17756304457, 13), -- Flaming Pink Horned Helmet
    (102887469225690, 14), -- Blue Inferno Skull
    (15381472359, 15), -- Solar System Aura
    (13241836994, 16), -- Verdant Crown
    (87983592197138, 17), -- Lord of the Buxeration
    (14524326503, 18), -- Quadri-Skull Aura
    (450557238, 19), -- 8-Bit Clockwork Shades
    (20011925, 20), -- Oozing Oscar
    (1080949, 21), -- Bunny Ears
    (1098282, 22), -- Lampshade
    (14463095, 23), -- Pinstripe Fedora
    (110673146052704, 24), -- Clockwork's Golden Shades
    (128217885, 25), -- Fall Fairy
    (382881521, 26), -- Tix Bling
    (1609390589, 27), -- Blue Traffic Cone
    (126892292563296, 28), -- Bandana From Beyond
    (113598419875472, 29), -- Helsworn Valkyrie
    (74029346462094, 30); -- Specter Time Fedora

DO $$
BEGIN
    IF (SELECT count(*) FROM "Items" i
        JOIN liro_limited_registration_order o ON i."RobloxAssetId" = o.asset_id
        WHERE i."ItemType" = 'Asset' AND i."CreatorId" = 1 AND i."CreatorType" = 'User'
          AND i."MarketStatus" IN ('Limited', 'LimitedUnique')) <> 30 THEN
        RAISE EXCEPTION 'Expected exactly 30 matching official Limited assets; no dates were changed.';
    END IF;
END $$;

WITH previous AS MATERIALIZED (
    SELECT i."Id", i."FirstSeenAt" AS old_first_seen_at, o.position
    FROM "Items" i
    JOIN liro_limited_registration_order o ON i."RobloxAssetId" = o.asset_id
    WHERE i."ItemType" = 'Asset' AND i."CreatorId" = 1 AND i."CreatorType" = 'User'
      AND i."MarketStatus" IN ('Limited', 'LimitedUnique')
    FOR UPDATE OF i
), updated AS (
    UPDATE "Items" i
    SET "FirstSeenAt" = transaction_timestamp() - (p.position - 1) * INTERVAL '1 second'
    FROM previous p
    WHERE i."Id" = p."Id"
    RETURNING p.position, i."RobloxAssetId", i."Name",
              p.old_first_seen_at, i."FirstSeenAt" AS new_first_seen_at
)
SELECT * FROM updated ORDER BY position;

ROLLBACK; -- Replace with COMMIT only when you want to persist the dates.