CREATE OR REPLACE TYPE t_money AS OBJECT (
  amount   NUMBER(18, 4),
  currency VARCHAR2(3),

  CONSTRUCTOR FUNCTION t_money(p_amount IN NUMBER) RETURN SELF AS RESULT,
  MEMBER FUNCTION plus(p_other IN t_money) RETURN t_money,
  MEMBER FUNCTION to_string RETURN VARCHAR2,
  MEMBER PROCEDURE round_to(p_decimals IN PLS_INTEGER DEFAULT 2),
  STATIC FUNCTION zero(p_currency IN VARCHAR2 DEFAULT 'TRY') RETURN t_money,
  ORDER MEMBER FUNCTION compare(p_other IN t_money) RETURN INTEGER
) NOT FINAL;
/

CREATE OR REPLACE TYPE BODY t_money AS

  CONSTRUCTOR FUNCTION t_money(p_amount IN NUMBER) RETURN SELF AS RESULT IS
  BEGIN
    SELF.amount := p_amount;
    SELF.currency := 'TRY';
    RETURN;
  END;

  MEMBER FUNCTION plus(p_other IN t_money) RETURN t_money IS
  BEGIN
    IF p_other.currency <> SELF.currency THEN
      RAISE_APPLICATION_ERROR(-20300, 'Currency mismatch: ' || SELF.currency || ' vs ' || p_other.currency);
    END IF;
    RETURN t_money(SELF.amount + p_other.amount, SELF.currency);
  END plus;

  MEMBER FUNCTION to_string RETURN VARCHAR2 IS
  BEGIN
    RETURN TO_CHAR(amount, 'FM999G999G990D00', 'NLS_NUMERIC_CHARACTERS='',.''') || ' ' || currency;
  END to_string;

  MEMBER PROCEDURE round_to(p_decimals IN PLS_INTEGER DEFAULT 2) IS
  BEGIN
    amount := ROUND(amount, p_decimals);
  END round_to;

  STATIC FUNCTION zero(p_currency IN VARCHAR2 DEFAULT 'TRY') RETURN t_money IS
  BEGIN
    RETURN t_money(0, p_currency);
  END zero;

  ORDER MEMBER FUNCTION compare(p_other IN t_money) RETURN INTEGER IS
  BEGIN
    RETURN SIGN(SELF.amount - p_other.amount);
  END compare;

END;
/

CREATE OR REPLACE TYPE t_money_list AS TABLE OF t_money;
/

CREATE OR REPLACE TYPE t_string_list AS VARRAY(50) OF VARCHAR2(4000);
/

CREATE OR REPLACE TYPE t_fee UNDER t_money (
  fee_code VARCHAR2(10),
  OVERRIDING MEMBER FUNCTION to_string RETURN VARCHAR2
);
/

CREATE OR REPLACE TYPE BODY t_fee AS
  OVERRIDING MEMBER FUNCTION to_string RETURN VARCHAR2 IS
  BEGIN
    RETURN fee_code || ': ' || (SELF AS t_money).to_string();
  END to_string;
END;
/

DECLARE
  l_total  t_money := t_money.zero();
  l_items  t_money_list := t_money_list(t_money(10.5), t_money(4.25, 'TRY'), t_money(100));
  l_names  t_string_list := t_string_list('a', 'b');
  l_extra  t_money_list := t_money_list();
  l_merged t_money_list;
BEGIN
  FOR i IN 1 .. l_items.COUNT LOOP
    l_total := l_total.plus(l_items(i));
  END LOOP;

  l_extra.EXTEND(2);
  l_extra(1) := t_money(1);
  l_extra(2) := NEW t_money(2, 'TRY');
  l_merged := l_items MULTISET UNION ALL l_extra;

  IF t_money(3) MEMBER OF l_merged THEN
    DBMS_OUTPUT.PUT_LINE('found');
  END IF;

  IF l_extra IS NOT EMPTY AND CARDINALITY(l_merged) > 3 THEN
    l_total.round_to(p_decimals => 0);
  END IF;

  SELECT t_money(SUM(t.amount), 'TRY')
    INTO l_total
    FROM TABLE(l_merged) t;

  SELECT CAST(MULTISET(SELECT t_money(x.amount, x.currency) FROM fees x WHERE x.active = 'Y') AS t_money_list)
    INTO l_merged
    FROM dual;

  l_names.TRIM;
  DBMS_OUTPUT.PUT_LINE(l_total.to_string() || ' / ' || l_names.COUNT || ' / ' || l_names.LIMIT);
END;
/
