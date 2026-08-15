export function naturalKey(sortText) {
  const pad = 12;
  /** @type {string[]} */
  const parts = [];
  let first = true;
  for (let i = 0; i < sortText.length; ) {
    const c = sortText[i];
    if ("- _/".includes(c)) {
      i++;
      continue;
    }
    if (!first) parts.push("|");
    first = false;
    if (c >= "0" && c <= "9") {
      let start = i;
      i++;
      while (i < sortText.length && sortText[i] >= "0" && sortText[i] <= "9") i++;
      parts.push("0:" + sortText.slice(start, i).padStart(pad, "0"));
    } else if (/[A-Za-z]/.test(c)) {
      let start = i;
      i++;
      while (i < sortText.length && /[A-Za-z]/.test(sortText[i])) i++;
      parts.push("1:" + sortText.slice(start, i).toLowerCase());
    } else {
      parts.push("1:" + c.toLowerCase());
      i++;
    }
  }
  return parts.join("");
}
