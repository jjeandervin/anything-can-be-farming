/** The organs Pl@ntNet accepts, in display order. Values are the exact wire strings sent to the API. */
export const PLANT_ORGANS = [
  { value: 'auto', label: 'Auto (let Pl@ntNet decide)', group: 'Common' },
  { value: 'leaf', label: 'Leaf', group: 'Common' },
  { value: 'flower', label: 'Flower', group: 'Common' },
  { value: 'fruit', label: 'Fruit', group: 'Common' },
  { value: 'bark', label: 'Bark', group: 'Common' },
  { value: 'habit', label: 'Whole plant', group: 'Common' },
  { value: 'branch', label: 'Branch', group: 'Common' },
  { value: 'bud', label: 'Bud', group: 'Common' },
  { value: 'seed', label: 'Seed', group: 'Common' },
  { value: 'other', label: 'Other', group: 'Common' },
  { value: 'scan', label: 'Scan', group: 'Specialist' },
  { value: 'sheet', label: 'Herbarium sheet', group: 'Specialist' },
  { value: 'drawing', label: 'Drawing', group: 'Specialist' },
  { value: 'anatomy', label: 'Anatomy (microscope)', group: 'Specialist' },
  { value: 'aerial', label: 'Aerial view', group: 'Specialist' },
] as const;

export type PlantOrgan = (typeof PLANT_ORGANS)[number]['value'];
export type PlantOrganGroup = (typeof PLANT_ORGANS)[number]['group'];

export const DEFAULT_ORGAN: PlantOrgan = 'auto';

export const PLANT_ORGAN_GROUPS: readonly { label: PlantOrganGroup; organs: (typeof PLANT_ORGANS)[number][] }[] =
  (['Common', 'Specialist'] as const).map(label => ({
    label,
    organs: PLANT_ORGANS.filter(organ => organ.group === label),
  }));

/** The display label for a wire value. Unknown values are shown as-is. */
export function organLabel(value: string): string {
  return PLANT_ORGANS.find(organ => organ.value === value)?.label ?? value;
}
