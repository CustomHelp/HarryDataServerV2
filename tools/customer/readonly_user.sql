-- ==========================================================================
--  readonly_user.sql  --  read-only DB access for the customer companion tools
--
--  Run ONCE on the MySQL server (as root/admin). Creates the GetData account
--  restricted to SELECT on camera_data, reachable from the customer network.
--  DO NOT run this automatically from any tool — it is an admin step.
--
--  1) Replace the password placeholder.
--  2) Replace the host pattern '10.0.%' with the customer's subnet (or a
--     specific PC IP). Use '%' only if you accept access from any host.
-- ==========================================================================

-- Read-only user, reachable from the customer subnet (adjust the host!).
CREATE USER IF NOT EXISTS 'GetData'@'10.0.%' IDENTIFIED BY '<STRONG_READONLY_PASSWORD>';
GRANT SELECT ON camera_data.* TO 'GetData'@'10.0.%';

FLUSH PRIVILEGES;

-- --------------------------------------------------------------------------
--  MySQL remote-access checklist (server side, do once):
--
--   a) my.ini  ->  [mysqld]  ->  bind-address = 0.0.0.0   (or the LAN IP)
--      then restart the MySQL service.
--   b) Windows Firewall: allow inbound TCP 3306 from the customer subnet only.
--   c) Verify from a client PC:
--        mysql -h <server-ip> -u GetData -p camera_data -e "SELECT 1;"
--   d) The write account (SettData) MUST stay 'SettData'@'localhost' only —
--      never expose it to the network.
-- --------------------------------------------------------------------------
