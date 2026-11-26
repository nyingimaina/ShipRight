-- 0005_user_time_zone (down): older builds never read the column, so removing it loses only this preference.

ALTER TABLE `User`
    DROP COLUMN IF EXISTS TimeZone;
