import { PERIODS, type Period } from '@/api/types'
import type { SelectOption } from '@/components/Select'

/** The bucket periods as select options, shortest first. */
export const PERIOD_OPTIONS: readonly SelectOption<Period>[] = PERIODS.map((period) => ({ value: period, label: period }))
