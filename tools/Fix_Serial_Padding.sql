-- =====================================================================================
-- One-time correction for the Serial1 padding mismatch (Problem 1).
--
-- Cause: before the SerialNumberHelper fix, camera measurements were stored with the
-- controller's trailing '0' padding (e.g. '2007261628430024167000', 22 chars) while
-- dmcserial stored the unpadded SPS serial (e.g. '2007261628430024167', 19 chars),
-- so the part-exit measurement lookup found no rows.
--
-- This script trims the padding from EXISTING rows in the measurement tables down to the
-- meaningful length (19 by default — set @len to match [General] SerialNumberLength).
-- It only trims when the characters past @len are ALL '0' (genuine padding), so a real
-- serial that legitimately ends in '0' within the meaningful length is never corrupted.
--
-- SAFETY:
--   * REVIEW the SELECT counts first (dry-run) before running the UPDATEs.
--   * Take a backup / run inside a transaction on a copy if the data matters.
--   * This is NOT run automatically by the application.
-- =====================================================================================

SET @len := 19;   -- must equal [General] SerialNumberLength

-- ---- 1) DRY RUN: how many rows would change, and a preview -----------------------------
SELECT 'measurements_serial' AS tbl, COUNT(*) AS rows_to_fix
FROM measurements_serial
WHERE CHAR_LENGTH(serial_number) > @len
  AND SUBSTRING(serial_number, @len + 1) REGEXP '^0+$';

SELECT DISTINCT serial_number AS before_value, LEFT(serial_number, @len) AS after_value
FROM measurements_serial
WHERE CHAR_LENGTH(serial_number) > @len
  AND SUBSTRING(serial_number, @len + 1) REGEXP '^0+$'
LIMIT 20;

SELECT 'measurements_serial_trimmer' AS tbl, COUNT(*) AS rows_to_fix
FROM measurements_serial_trimmer
WHERE CHAR_LENGTH(serial_trimmer) > @len
  AND SUBSTRING(serial_trimmer, @len + 1) REGEXP '^0+$';

SELECT DISTINCT serial_trimmer AS before_value, LEFT(serial_trimmer, @len) AS after_value
FROM measurements_serial_trimmer
WHERE CHAR_LENGTH(serial_trimmer) > @len
  AND SUBSTRING(serial_trimmer, @len + 1) REGEXP '^0+$'
LIMIT 20;

-- ---- 2) APPLY (uncomment after reviewing the dry-run above) ----------------------------
-- UPDATE measurements_serial
-- SET serial_number = LEFT(serial_number, @len)
-- WHERE CHAR_LENGTH(serial_number) > @len
--   AND SUBSTRING(serial_number, @len + 1) REGEXP '^0+$';
--
-- UPDATE measurements_serial_trimmer
-- SET serial_trimmer = LEFT(serial_trimmer, @len)
-- WHERE CHAR_LENGTH(serial_trimmer) > @len
--   AND SUBSTRING(serial_trimmer, @len + 1) REGEXP '^0+$';

-- ---- 3) (Optional) verify a known part links up after the fix --------------------------
-- SELECT d.serial_number, COUNT(m.id) AS measurement_rows
-- FROM dmcserial d
-- LEFT JOIN measurements_serial m ON m.serial_number = d.serial_number
-- GROUP BY d.serial_number
-- ORDER BY d.created_at DESC
-- LIMIT 20;
