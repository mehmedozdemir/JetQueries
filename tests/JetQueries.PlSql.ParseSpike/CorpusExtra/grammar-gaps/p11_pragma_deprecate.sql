CREATE OR REPLACE PACKAGE p AS
  PROCEDURE old_api(p_id IN NUMBER);
  PRAGMA DEPRECATE(old_api, 'Use new_api instead');
END p;
/
