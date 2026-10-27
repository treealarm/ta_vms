-- Enrollment starts open, so that a new deployment is set up without a password, but 0005 opened
-- it on every database it ran on, including that of a core with nodes joined by package long before:
-- any machine reaching its web could then fetch the core's passwords until the administrator found
-- the new switch. A database VmsCfg has started on before belongs to a running installation, and
-- its administrator opens enrollment when a machine is to be added. VmsCfg seeds a MediaMTX row and
-- a storage at every start, after the migrations, so on a new database both tables are still empty.
UPDATE deployment SET enrollment_open = false
WHERE EXISTS (SELECT 1 FROM media_mtx_server) OR EXISTS (SELECT 1 FROM storage);
