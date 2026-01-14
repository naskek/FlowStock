from fastapi import HTTPException, status


def extract_digits(s: str) -> str:
    return "".join(ch for ch in s if ch.isdigit())


def compute_gs1_mod10_check_digit(base: str) -> str:
    if not base.isdigit() or len(base) != 17:
        raise ValueError("Base must be 17 digits")
    total = 0
    weight = 3
    for ch in reversed(base):
        total += int(ch) * weight
        weight = 1 if weight == 3 else 3
    check = (10 - (total % 10)) % 10
    return str(check)


def normalize_sscc(scan: str) -> str:
    digits = extract_digits(scan)
    if digits.startswith("00") and len(digits) == 20:
        sscc = digits[2:]
    elif len(digits) == 18:
        sscc = digits
    else:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Invalid SSCC format")
    validate_sscc(sscc, validate_cd=True)
    return sscc


def validate_sscc(sscc18: str, validate_cd: bool = True) -> None:
    if not sscc18.isdigit() or len(sscc18) != 18:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Invalid SSCC")
    if not validate_cd:
        return
    base = sscc18[:-1]
    cd = sscc18[-1]
    expected = compute_gs1_mod10_check_digit(base)
    if cd != expected:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Invalid SSCC check digit")
