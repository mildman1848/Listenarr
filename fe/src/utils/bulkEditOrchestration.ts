/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import type { Audiobook, AudibleBookMetadata } from '@/types'

export interface BulkEditItemResult {
  id: number
  success: boolean
  errors: string[]
}

export interface BulkEditOrchestrationRequest {
  ids: number[]
  updates: Record<string, boolean | number | string>
  destinationRoot?: string | null
  moveFiles: boolean
  deleteEmptySource: boolean
}

export interface BulkEditOrchestrationDependencies {
  getAudiobook(id: number): Promise<Audiobook>
  previewLibraryPath(
    metadata: AudibleBookMetadata,
    destinationRoot?: string,
  ): Promise<{ fullPath: string; relativePath: string; root?: string }>
  bulkUpdateAudiobooks(
    ids: number[],
    updates: Record<string, boolean | number | string>,
  ): Promise<{ message: string; results: BulkEditItemResult[] }>
  moveAudiobook(
    id: number,
    destinationPath: string,
    options: { sourcePath: string; moveFiles: boolean; deleteEmptySource: boolean },
  ): Promise<{ message: string; jobId?: string }>
  trackQueuedJob(job: { jobId: string; audiobookId: number; target: string }): void
}

interface PhysicalMovePlan {
  id: number
  sourcePath: string
  destinationPath: string
}

export async function executeBulkEdit(
  request: BulkEditOrchestrationRequest,
  dependencies: BulkEditOrchestrationDependencies,
): Promise<{ results: BulkEditItemResult[] }> {
  const metadataUpdates = { ...request.updates }
  delete metadataUpdates.moveFiles
  delete metadataUpdates.deleteEmptySource
  if (request.moveFiles) {
    delete metadataUpdates.rootFolder
  }

  const plans = new Map<number, PhysicalMovePlan>()
  const planningErrors = new Map<number, string>()
  if (request.moveFiles) {
    const destinationRoot = request.destinationRoot?.trim()
    if (!destinationRoot) {
      for (const id of request.ids) planningErrors.set(id, 'A destination root is required.')
    } else {
      await Promise.all(
        request.ids.map(async (id) => {
          try {
            const audiobook = await dependencies.getAudiobook(id)
            const sourcePath = audiobook.basePath?.trim()
            if (!sourcePath) {
              throw new Error('The audiobook has no current BasePath to move from.')
            }

            const preview = await dependencies.previewLibraryPath(
              toPreviewMetadata(audiobook),
              destinationRoot,
            )
            if (!preview.fullPath?.trim()) {
              throw new Error('The destination preview did not return a full path.')
            }

            plans.set(id, {
              id,
              sourcePath,
              destinationPath: preview.fullPath,
            })
          } catch (error) {
            planningErrors.set(id, errorMessage(error))
          }
        }),
      )
    }
  }

  const metadataResponse =
    Object.keys(metadataUpdates).length > 0
      ? await dependencies.bulkUpdateAudiobooks(request.ids, metadataUpdates)
      : {
          message: 'No metadata changes requested.',
          results: request.ids.map((id) => ({ id, success: true, errors: [] as string[] })),
        }
  const resultsById = new Map<number, BulkEditItemResult>(
    metadataResponse.results.map((result) => [
      result.id,
      {
        id: result.id,
        success: result.success,
        errors: [...(result.errors ?? [])],
      },
    ]),
  )

  for (const id of request.ids) {
    const result =
      resultsById.get(id) ??
      ({ id, success: false, errors: ['The bulk update returned no result for this audiobook.'] } as const)
    resultsById.set(id, {
      id: result.id,
      success: result.success,
      errors: [...result.errors],
    })
  }

  if (!request.moveFiles) {
    return { results: request.ids.map((id) => resultsById.get(id)!) }
  }

  for (const id of request.ids) {
    const result = resultsById.get(id)!
    const planningError = planningErrors.get(id)
    if (planningError) {
      result.success = false
      result.errors.push(planningError)
      continue
    }
    if (!result.success) continue

    const plan = plans.get(id)
    if (!plan) {
      result.success = false
      result.errors.push('The physical move could not be planned.')
      continue
    }

    try {
      const queued = await dependencies.moveAudiobook(id, plan.destinationPath, {
        sourcePath: plan.sourcePath,
        moveFiles: true,
        deleteEmptySource: request.deleteEmptySource,
      })
      if (!queued.jobId?.trim()) {
        throw new Error('The server did not return a durable move job ID.')
      }

      dependencies.trackQueuedJob({
        jobId: queued.jobId,
        audiobookId: id,
        target: plan.destinationPath,
      })
    } catch (error) {
      result.success = false
      result.errors.push(errorMessage(error))
    }
  }

  return { results: request.ids.map((id) => resultsById.get(id)!) }
}

function toPreviewMetadata(audiobook: Audiobook): AudibleBookMetadata {
  return {
    title: audiobook.title,
    subtitle: audiobook.subtitle,
    authors: audiobook.authors ?? [],
    publishedDate: audiobook.publishedDate,
    publishYear: audiobook.publishYear,
    series: audiobook.series,
    seriesNumber: audiobook.seriesNumber,
    seriesMemberships: audiobook.seriesMemberships,
    description: audiobook.description,
    genres: audiobook.genres,
    tags: audiobook.tags,
    narrators: audiobook.narrators,
    isbn: audiobook.isbn,
    asin: audiobook.asin ?? '',
    publisher: audiobook.publisher,
    language: audiobook.language,
    runtime: audiobook.runtime,
    edition: audiobook.edition,
    version: audiobook.version,
    imageUrl: audiobook.imageUrl,
    explicit: audiobook.explicit,
    abridged: audiobook.abridged,
    openLibraryId: audiobook.openLibraryId,
    qualityProfileId: audiobook.qualityProfileId,
  }
}

function errorMessage(error: unknown): string {
  if (error instanceof Error && error.message.trim()) return error.message
  return 'The bulk physical move could not be completed.'
}
