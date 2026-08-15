//! Port of SearchEngine.Sharp `NaturalSortKeyBuilder`.

pub fn natural_key(sort_text: &str) -> String {
    const NUMERIC_PADDING: usize = 12;
    const MAX_CHARS_PER_INPUT_CHAR: usize = NUMERIC_PADDING + 3;

    if sort_text.is_empty() {
        return String::new();
    }

    let max_len = sort_text.len() * MAX_CHARS_PER_INPUT_CHAR;
    let mut buffer = vec![0u8; max_len];
    let mut pos = 0usize;
    let bytes = sort_text.as_bytes();
    let mut first = true;
    let mut i = 0usize;

    while i < bytes.len() {
        let c = bytes[i] as char;
        if matches!(c, '-' | ' ' | '_' | '/') {
            i += 1;
            continue;
        }
        if !first {
            buffer[pos] = b'|';
            pos += 1;
        }
        first = false;

        if c.is_ascii_digit() {
            let start = i;
            i += 1;
            while i < bytes.len() && (bytes[i] as char).is_ascii_digit() {
                i += 1;
            }
            buffer[pos] = b'0';
            pos += 1;
            buffer[pos] = b':';
            pos += 1;
            let digit_count = i - start;
            for _ in digit_count..NUMERIC_PADDING {
                buffer[pos] = b'0';
                pos += 1;
            }
            buffer[pos..pos + digit_count].copy_from_slice(&bytes[start..i]);
            pos += digit_count;
        } else if c.is_ascii_alphabetic() {
            let start = i;
            i += 1;
            while i < bytes.len() && (bytes[i] as char).is_ascii_alphabetic() {
                i += 1;
            }
            buffer[pos] = b'1';
            pos += 1;
            buffer[pos] = b':';
            pos += 1;
            for &b in &bytes[start..i] {
                buffer[pos] = b.to_ascii_lowercase();
                pos += 1;
            }
        } else {
            buffer[pos] = b'1';
            pos += 1;
            buffer[pos] = b':';
            pos += 1;
            buffer[pos] = c.to_ascii_lowercase() as u8;
            pos += 1;
            i += 1;
        }
    }

    String::from_utf8(buffer[..pos].to_vec()).unwrap()
}
