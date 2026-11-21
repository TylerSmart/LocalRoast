export interface ContiguousSectionGroup<T> {
  index: number;
  sectionName: string;
  hasNamedSiblings: boolean;
  isContinuationAfterNamed: boolean;
  items: T[];
}

export function groupContiguousBySection<T>(
  items: readonly T[],
  getSection: (item: T) => string | null | undefined,
): ContiguousSectionGroup<T>[] {
  if (items.length === 0) {
    return [];
  }

  const rawGroups: { sectionName: string; normalizedKey: string; items: T[] }[] = [];
  let hasAnyNamed = false;

  for (const item of items) {
    const raw = getSection(item)?.trim() ?? '';
    const key = raw.toLowerCase();
    if (key.length > 0) {
      hasAnyNamed = true;
    }

    const current = rawGroups.at(-1);
    if (current && current.normalizedKey === key) {
      current.items.push(item);
    } else {
      rawGroups.push({
        sectionName: raw,
        normalizedKey: key,
        items: [item],
      });
    }
  }

  let seenNamedGroup = false;
  return rawGroups.map((group, index) => {
    const isNamed = group.normalizedKey.length > 0;
    const isContinuationAfterNamed = !isNamed && seenNamedGroup;
    if (isNamed) {
      seenNamedGroup = true;
    }
    return {
      index,
      sectionName: group.sectionName,
      hasNamedSiblings: hasAnyNamed,
      isContinuationAfterNamed,
      items: group.items,
    };
  });
}
