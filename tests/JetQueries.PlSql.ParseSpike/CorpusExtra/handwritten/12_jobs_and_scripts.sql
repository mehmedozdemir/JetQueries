-- SQL*Plus style deployment script.
SET SERVEROUTPUT ON SIZE UNLIMITED
SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE ROLLBACK

PROMPT Creating scheduler objects

BEGIN
  DBMS_SCHEDULER.CREATE_PROGRAM(
    program_name        => 'SETTLEMENT_PROG',
    program_type        => 'STORED_PROCEDURE',
    program_action      => 'SETTLEMENT_ETL.RUN_DAILY',
    number_of_arguments => 1,
    enabled             => FALSE,
    comments            => 'Daily settlement load');

  DBMS_SCHEDULER.DEFINE_PROGRAM_ARGUMENT(
    program_name      => 'SETTLEMENT_PROG',
    argument_position => 1,
    argument_name     => 'P_RUN_DATE',
    argument_type     => 'DATE',
    default_value     => NULL);

  DBMS_SCHEDULER.CREATE_JOB(
    job_name        => 'SETTLEMENT_JOB',
    program_name    => 'SETTLEMENT_PROG',
    start_date      => TRUNC(SYSTIMESTAMP) + INTERVAL '1 02:30:00' DAY TO SECOND,
    repeat_interval => 'FREQ=DAILY; BYHOUR=2; BYMINUTE=30',
    end_date        => NULL,
    enabled         => TRUE,
    auto_drop       => FALSE);
EXCEPTION
  WHEN OTHERS THEN
    IF SQLCODE = -27477 THEN
      DBMS_OUTPUT.PUT_LINE('Job already exists');
    ELSE
      RAISE;
    END IF;
END;
/

DECLARE
  l_job_no BINARY_INTEGER;
BEGIN
  DBMS_JOB.SUBMIT(
    job       => l_job_no,
    what      => 'purge_old_logs(p_keep_days => 30);',
    next_date => SYSDATE + 1 / 24,
    interval  => 'TRUNC(SYSDATE) + 1 + 3/24');
  COMMIT;
  DBMS_OUTPUT.PUT_LINE('Submitted job ' || l_job_no);
END;
/

VARIABLE v_count NUMBER
EXEC :v_count := 0

BEGIN
  SELECT COUNT(*) INTO :v_count FROM user_scheduler_jobs WHERE job_name LIKE 'SETTLEMENT%';
END;
/

PRINT v_count

CREATE OR REPLACE PROCEDURE notify_ops(p_subject IN VARCHAR2, p_body IN CLOB) IS
  l_conn  UTL_SMTP.CONNECTION;
  l_reply UTL_SMTP.REPLY;
  c_crlf  CONSTANT VARCHAR2(2) := UTL_TCP.CRLF;
  l_req   UTL_HTTP.REQ;
  l_resp  UTL_HTTP.RESP;
  l_chunk VARCHAR2(32767);
BEGIN
  l_conn := UTL_SMTP.OPEN_CONNECTION(host => 'smtp.example.internal', port => 25, tx_timeout => 10);
  l_reply := UTL_SMTP.HELO(l_conn, 'db.example.internal');
  UTL_SMTP.MAIL(l_conn, 'noreply@example.com');
  UTL_SMTP.RCPT(l_conn, 'ops@example.com');
  UTL_SMTP.OPEN_DATA(l_conn);
  UTL_SMTP.WRITE_DATA(l_conn, 'Subject: ' || p_subject || c_crlf || c_crlf);
  UTL_SMTP.WRITE_RAW_DATA(l_conn, UTL_RAW.CAST_TO_RAW(DBMS_LOB.SUBSTR(p_body, 4000, 1)));
  UTL_SMTP.CLOSE_DATA(l_conn);
  UTL_SMTP.QUIT(l_conn);

  l_req := UTL_HTTP.BEGIN_REQUEST('https://hooks.example.internal/ops', 'POST', 'HTTP/1.1');
  UTL_HTTP.SET_HEADER(l_req, 'Content-Type', 'application/json; charset=utf-8');
  UTL_HTTP.WRITE_TEXT(l_req, '{"subject":"' || REPLACE(p_subject, '"', '\"') || '"}');
  l_resp := UTL_HTTP.GET_RESPONSE(l_req);
  BEGIN
    LOOP
      UTL_HTTP.READ_TEXT(l_resp, l_chunk, 32767);
    END LOOP;
  EXCEPTION
    WHEN UTL_HTTP.END_OF_BODY THEN
      UTL_HTTP.END_RESPONSE(l_resp);
  END;
EXCEPTION
  WHEN UTL_SMTP.TRANSIENT_ERROR OR UTL_SMTP.PERMANENT_ERROR THEN
    BEGIN
      UTL_SMTP.QUIT(l_conn);
    EXCEPTION
      WHEN OTHERS THEN
        NULL;
    END;
    RAISE_APPLICATION_ERROR(-20600, 'Mail failed: ' || SQLERRM);
END notify_ops;
/
SHOW ERRORS

CALL notify_ops('Deployment finished', TO_CLOB('All objects created.'));
EXECUTE DBMS_UTILITY.COMPILE_SCHEMA(schema => USER, compile_all => FALSE);
ALTER PACKAGE card_ops COMPILE BODY;
ALTER PROCEDURE notify_ops COMPILE DEBUG;
ALTER SESSION SET NLS_LANGUAGE = 'TURKISH' NLS_TERRITORY = 'TURKEY';
ALTER SYSTEM FLUSH SHARED_POOL;

COMMIT;
EXIT
