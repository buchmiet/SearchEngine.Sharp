export function globMatch(pattern, word) {
  const p = [...pattern];
  const w = [...word];
  let patternIndex = 0;
  let wordIndex = 0;
  let starPatternIndex = -1;
  let starWordIndex = 0;

  while (wordIndex < w.length) {
    if (patternIndex < p.length) {
      const patternChar = p[patternIndex];
      if (patternChar === "*") {
        starPatternIndex = patternIndex;
        starWordIndex = wordIndex;
        patternIndex++;
        while (patternIndex < p.length && p[patternIndex] === "*") patternIndex++;
        continue;
      }
      if (patternChar === "?" || patternChar === w[wordIndex]) {
        patternIndex++;
        wordIndex++;
        continue;
      }
    }
    if (starPatternIndex >= 0) {
      starWordIndex++;
      wordIndex = starWordIndex;
      patternIndex = starPatternIndex + 1;
      while (patternIndex < p.length && p[patternIndex] === "*") patternIndex++;
      continue;
    }
    return false;
  }

  while (patternIndex < p.length && p[patternIndex] === "*") patternIndex++;
  return patternIndex === p.length;
}
