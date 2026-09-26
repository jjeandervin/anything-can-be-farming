import { describe, expect, it } from 'vitest';
import { DEFAULT_ORGAN, PLANT_ORGAN_GROUPS, PLANT_ORGANS, organLabel } from './plant-organ';

describe('plant organs', () => {
  it('has 15 unique wire values in the documented order', () => {
    const values = PLANT_ORGANS.map(organ => organ.value);
    expect(values).toHaveLength(15);
    expect(new Set(values).size).toBe(15);
    expect(values).toEqual([
      'auto', 'leaf', 'flower', 'fruit', 'bark', 'habit', 'branch', 'bud', 'seed', 'other',
      'scan', 'sheet', 'drawing', 'anatomy', 'aerial',
    ]);
  });

  it('defaults to auto', () => {
    expect(DEFAULT_ORGAN).toBe('auto');
  });

  it('groups common organs before specialist ones', () => {
    expect(PLANT_ORGAN_GROUPS.map(group => [group.label, group.organs.length])).toEqual([
      ['Common', 10], ['Specialist', 5],
    ]);
  });

  it('labels wire values and passes unknown values through', () => {
    expect(organLabel('habit')).toBe('Whole plant');
    expect(organLabel('stem')).toBe('stem');
  });
});
