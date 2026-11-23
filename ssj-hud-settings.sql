-- SSJ-Plugin 2.2.0 (Next-il/Sharpyku-SSJ): the HUD choices each player makes in !ssj.
--
-- Adds one column to the existing settings table (config.json Database.TableName, ssj_settings by default, in the
-- database SSJ already uses). Run it once, before or after the deploy: until it exists SSJ logs one warning at load
-- and keeps saving SSJ on/off, repeat and jumps as before, while the HUD choices last until the player leaves.
-- A fresh install creates the column itself (CREATE TABLE IF NOT EXISTS); an existing table is never altered by the
-- plugin.
--
-- Hud holds only what differs from config.json's Hud defaults, for example
--   o=both;p=xr;s=l;h=0;a=lime
-- o = where the stats go (hud|chat|both), p = spot (xl left of the crosshair, xr right of it, xa above it,
-- tc top center, rm right side, br next to the timer card), s = size (s|m|l), h = seconds the card stays after the
-- run's last jump (3|6|10, 0 = until the next run), a = accent (ice|orange|pink|lime|white).
-- An empty value is a player on every default. Existing rows need nothing: '' is the default.

-- MariaDB
ALTER TABLE ssj_settings ADD COLUMN IF NOT EXISTS Hud VARCHAR(128) NOT NULL DEFAULT '';

-- MySQL 8 (no IF NOT EXISTS on ADD COLUMN; skip it if the column is already there)
-- ALTER TABLE ssj_settings ADD COLUMN Hud VARCHAR(128) NOT NULL DEFAULT '';
