DECLARE
  TYPE t_point IS RECORD (x NUMBER, y NUMBER);
  TYPE t_names IS TABLE OF VARCHAR2(30) INDEX BY VARCHAR2(10);
  p t_point := t_point(x => 0, y => 0);
  n t_names := t_names('tr' => 'Turkiye', 'de' => 'Deutschland');
BEGIN
  NULL;
END;
/
