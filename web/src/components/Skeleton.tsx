import { cn } from '@/lib/utils'

/** A placeholder block while data loads; the shimmer stops under reduced motion. */
export function Skeleton({ className }: { className?: string }) {
  return <span aria-hidden="true" className={cn('skeleton block rounded-md', className)} />
}
