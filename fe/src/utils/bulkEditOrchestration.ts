/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

export type BulkPathChangeMode = 'None' | 'MetadataOnly' | 'Physical'

export interface BulkPathChangeRequest {
  mode: BulkPathChangeMode
  destinationRootOrPath?: string | null
  deleteEmptySource: boolean
}

export interface BulkEditItemResult {
  id: number
  success: boolean
  metadataUpdated?: boolean
  pathChangeOutcome?: string
  moveJobId?: string | null
  resolvedDestination?: string | null
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
  bulkUpdateAudiobooks(
    ids: number[],
    updates: Record<string, boolean | number | string>,
    pathChange?: BulkPathChangeRequest,
  ): Promise<{ message: string; results: BulkEditItemResult[] }>
  trackQueuedJob(job: { jobId: string; audiobookId: number; target: string }): void
}

export async function executeBulkEdit(
  request: BulkEditOrchestrationRequest,
  dependencies: BulkEditOrchestrationDependencies,
): Promise<{ results: BulkEditItemResult[] }> {
  const metadataUpdates = { ...request.updates }
  delete metadataUpdates.moveFiles
  delete metadataUpdates.deleteEmptySource

  const requestedRoot = request.destinationRoot?.trim() || null
  const rootChangeRequested = requestedRoot !== null || 'rootFolder' in metadataUpdates
  const destinationRoot = requestedRoot ?? stringValue(metadataUpdates.rootFolder)
  delete metadataUpdates.rootFolder

  let pathChange: BulkPathChangeRequest | undefined
  if (rootChangeRequested) {
    pathChange = {
      mode: request.moveFiles ? 'Physical' : 'MetadataOnly',
      destinationRootOrPath: destinationRoot,
      deleteEmptySource: request.deleteEmptySource,
    }
  }

  const response = await dependencies.bulkUpdateAudiobooks(request.ids, metadataUpdates, pathChange)
  const resultsById = new Map<number, BulkEditItemResult>(
    response.results.map((result) => [
      result.id,
      {
        ...result,
        errors: [...(result.errors ?? [])],
      },
    ]),
  )

  for (const id of request.ids) {
    const result = resultsById.get(id) ?? {
      id,
      success: false,
      errors: ['The bulk update returned no result for this audiobook.'],
    }
    resultsById.set(id, result)

    if (!request.moveFiles || !result.success) continue

    const jobId = result.moveJobId?.trim()
    const target = result.resolvedDestination?.trim()
    if (!jobId || !target) {
      result.success = false
      result.errors.push(
        !jobId
          ? 'The server did not return a durable move job ID.'
          : 'The server did not return the resolved move destination.',
      )
      continue
    }

    dependencies.trackQueuedJob({
      jobId,
      audiobookId: id,
      target,
    })
  }

  return { results: request.ids.map((id) => resultsById.get(id)!) }
}

function stringValue(value: boolean | number | string | undefined): string | null {
  return typeof value === 'string' && value.trim() ? value.trim() : null
}
