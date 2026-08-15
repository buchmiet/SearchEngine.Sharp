//! Glob matcher aligned with SearchEngine.Sharp `GlobMatcher` (anchored full-string match).

pub fn glob_match(pattern: &str, word: &str) -> bool {
    let pattern = pattern.as_bytes();
    let word = word.as_bytes();
    let mut pattern_index = 0usize;
    let mut word_index = 0usize;
    let mut star_pattern_index: isize = -1;
    let mut star_word_index = 0usize;

    while word_index < word.len() {
        if pattern_index < pattern.len() {
            let pattern_char = pattern[pattern_index];
            if pattern_char == b'*' {
                star_pattern_index = pattern_index as isize;
                star_word_index = word_index;
                pattern_index += 1;
                while pattern_index < pattern.len() && pattern[pattern_index] == b'*' {
                    pattern_index += 1;
                }
                continue;
            }
            if pattern_char == b'?' || pattern_char == word[word_index] {
                pattern_index += 1;
                word_index += 1;
                continue;
            }
        }

        if star_pattern_index >= 0 {
            star_word_index += 1;
            word_index = star_word_index;
            pattern_index = (star_pattern_index + 1) as usize;
            while pattern_index < pattern.len() && pattern[pattern_index] == b'*' {
                pattern_index += 1;
            }
            continue;
        }

        return false;
    }

    while pattern_index < pattern.len() && pattern[pattern_index] == b'*' {
        pattern_index += 1;
    }

    pattern_index == pattern.len()
}
