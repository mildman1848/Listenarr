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
import { createPinia, setActivePinia } from 'pinia'
import { apiService } from '@/services/api'
import { useRootFoldersStore } from '@/stores/rootFolders'
import type { RootFolderPathChangeResult } from '@/types'

describe('root folder relocation reauthorization store action', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setActivePinia(createPinia())
  })

  it('passes the exact confirmed target path and reloads root folders', async () => {
    const targetPath = '/srv/Audiobooks '
    const result: RootFolderPathChangeResult = {
      relocationId: 'relocation-1',
      rootFolderId: 3,
      currentPath: '/srv/Old',
      targetPath,
      status: 'Running',
      totalJobs: 1,
      completedJobs: 0,
      targetIdentityEnrollmentState: 'Authorized',
    }
    vi.mocked(apiService.reauthorizeLegacyRootFolderRelocationTarget).mockResolvedValueOnce(result)
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([])
    const store = useRootFoldersStore()

    await expect(store.reauthorizeLegacyTarget('relocation-1', targetPath)).resolves.toEqual(result)

    expect(apiService.reauthorizeLegacyRootFolderRelocationTarget).toHaveBeenCalledWith(
      'relocation-1',
      targetPath,
    )
    expect(apiService.getRootFolders).toHaveBeenCalledTimes(1)
  })
})
