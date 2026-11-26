-- 0005_user_time_zone (up): each person's own time zone for dates and schedules.
--
-- Null/empty means "use the app default" (Africa/Nairobi), so existing accounts need no data change and a database
-- written by an older build still reads cleanly. Safe to run again.

ALTER TABLE `User`
    ADD COLUMN IF NOT EXISTS TimeZone VARCHAR(100) NULL;
