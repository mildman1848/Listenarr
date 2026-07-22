/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { executeBulkEdit } from '@/utils/bulkEditOrchestration'
import type { Audiobook } from '@/types'

const books: Record<number, Audiobook> = {
  1: {
    id: 1,
    title: 'Book One',
    authors: ['Author One'],
    asin: 'B000000001',
    basePath: '/library/Author One/Book One',
  },
  2: {
    id: 2,
    title: 'Book Two',
    authors: ['Author Two'],
    asin: 'B000000002',
    basePath: '/library/Author Two/Book Two',
  },
}

function createDependencies() {
  return {
    getAudiobook: vi.fn(async (id: number) => books[id]),
    previewLibraryPath: vi.fn(async (metadata: { title: string }, destinationRoot?: string) => ({
      fullPath: `${destinationRoot}/${metadata.title}`,
      relativePath: metadata.title,
      root: destinationRoot,
    })),
    bulkUpdateAudiobooks: vi.fn(async (ids: number[]) => ({
      message: 'updated',
      results: ids.map((id) => ({ id, success: true, errors: [] as string[] })),
    })),
    moveAudiobook: vi.fn(async (id: number) => ({
      message: 'queued',
      jobId: `job-${id}`,
    })),
    trackQueuedJob: vi.fn(),
  }
}

describe('bulk edit orchestration', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('queues physical moves from original paths without pre-saving root metadata', async () => {
    const dependencies = createDependencies()

    const outcome = await executeBulkEdit(
      {
        ids: [1, 2],
        updates: {
          monitored: true,
          rootFolder: '/library-new',
          moveFiles: true,
          deleteEmptySource: true,
        },
        destinationRoot: '/library-new',
        moveFiles: true,
        deleteEmptySource: true,
      },
      dependencies,
    )

    expect(dependencies.bulkUpdateAudiobooks).toHaveBeenCalledWith([1, 2], {
      monitored: true,
    })
    expect(dependencies.previewLibraryPath).toHaveBeenNthCalledWith(
      1,
      expect.objectContaining({ title: 'Book One', authors: ['Author One'] }),
      '/library-new',
    )
    expect(dependencies.moveAudiobook).toHaveBeenNthCalledWith(
      1,
      1,
      '/library-new/Book One',
      {
        sourcePath: '/library/Author One/Book One',
        moveFiles: true,
        deleteEmptySource: true,
      },
    )
    expect(dependencies.trackQueuedJob).toHaveBeenCalledWith({
      jobId: 'job-1',
      audiobookId: 1,
      target: '/library-new/Book One',
    })
    expect(outcome.results).toEqual([
      { id: 1, success: true, errors: [] },
      { id: 2, success: true, errors: [] },
    ])
  })

  it('surfaces a per-item physical enqueue failure instead of reporting success', async () => {
    const dependencies = createDependencies()
    dependencies.moveAudiobook.mockImplementation(async (id: number) => {
      if (id === 2) throw new Error('queue unavailable')
      return { message: 'queued', jobId: `job-${id}` }
    })

    const outcome = await executeBulkEdit(
      {
        ids: [1, 2],
        updates: { rootFolder: '/library-new', moveFiles: true },
        destinationRoot: '/library-new',
        moveFiles: true,
        deleteEmptySource: false,
      },
      dependencies,
    )

    expect(outcome.results).toEqual([
      { id: 1, success: true, errors: [] },
      { id: 2, success: false, errors: ['queue unavailable'] },
    ])
    expect(dependencies.trackQueuedJob).toHaveBeenCalledTimes(1)
    expect(dependencies.trackQueuedJob).not.toHaveBeenCalledWith(
      expect.objectContaining({ audiobookId: 2 }),
    )
  })

  it('keeps metadata-only root changes in the bulk request and does not queue moves', async () => {
    const dependencies = createDependencies()

    await executeBulkEdit(
      {
        ids: [1],
        updates: { monitored: false, rootFolder: '/library-new' },
        destinationRoot: '/library-new',
        moveFiles: false,
        deleteEmptySource: false,
      },
      dependencies,
    )

    expect(dependencies.bulkUpdateAudiobooks).toHaveBeenCalledWith([1], {
      monitored: false,
      rootFolder: '/library-new',
    })
    expect(dependencies.getAudiobook).not.toHaveBeenCalled()
    expect(dependencies.previewLibraryPath).not.toHaveBeenCalled()
    expect(dependencies.moveAudiobook).not.toHaveBeenCalled()
    expect(dependencies.trackQueuedJob).not.toHaveBeenCalled()
  })
})
