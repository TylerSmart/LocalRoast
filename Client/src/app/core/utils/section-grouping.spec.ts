import { describe, expect, it } from 'vitest';
import { groupContiguousBySection } from './section-grouping';

interface TestItem {
  id: number;
  section?: string | null;
}

describe('groupContiguousBySection', () => {
  it('returns empty array for empty input', () => {
    expect(groupContiguousBySection([], (x: TestItem) => x.section)).toEqual([]);
  });

  it('keeps single unnamed group cleanly unmarked when no named sections exist', () => {
    const result = groupContiguousBySection(
      [
        { id: 1, section: null },
        { id: 2, section: '  ' },
      ],
      (x) => x.section,
    );

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      index: 0,
      sectionName: '',
      hasNamedSiblings: false,
      isContinuationAfterNamed: false,
    });
    expect(result[0].items.map((x) => x.id)).toEqual([1, 2]);
  });

  it('preserves chronological order without merging non-adjacent unnamed runs', () => {
    const result = groupContiguousBySection(
      [
        { id: 1, section: null },
        { id: 2, section: 'For the Sauce' },
        { id: 3, section: 'for the sauce ' },
        { id: 4, section: null },
        { id: 5, section: 'For the Topping' },
        { id: 6, section: '' },
      ],
      (x) => x.section,
    );

    expect(
      result.map((g) => ({
        index: g.index,
        sectionName: g.sectionName,
        ids: g.items.map((x) => x.id),
        hasNamedSiblings: g.hasNamedSiblings,
        isContinuationAfterNamed: g.isContinuationAfterNamed,
      })),
    ).toEqual([
      {
        index: 0,
        sectionName: '',
        ids: [1],
        hasNamedSiblings: true,
        isContinuationAfterNamed: false,
      },
      {
        index: 1,
        sectionName: 'For the Sauce',
        ids: [2, 3],
        hasNamedSiblings: true,
        isContinuationAfterNamed: false,
      },
      {
        index: 2,
        sectionName: '',
        ids: [4],
        hasNamedSiblings: true,
        isContinuationAfterNamed: true,
      },
      {
        index: 3,
        sectionName: 'For the Topping',
        ids: [5],
        hasNamedSiblings: true,
        isContinuationAfterNamed: false,
      },
      {
        index: 4,
        sectionName: '',
        ids: [6],
        hasNamedSiblings: true,
        isContinuationAfterNamed: true,
      },
    ]);
  });
});
