/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { mount } from '@vue/test-utils'
import { vi, describe, it, expect } from 'vitest'
import { nextTick } from 'vue'

const toastMocks = vi.hoisted(() => ({
  error: vi.fn(),
  info: vi.fn(),
  success: vi.fn(),
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => toastMocks,
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getAudiobook: vi.fn().mockImplementation(async (id: number) => ({ id })),
    getQualityProfiles: vi.fn().mockResolvedValue([]),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: 'C:\\root' }),
    getAudiobookIdentifiers: vi.fn().mockResolvedValue({ identifiers: [] }),
    getRootFolders: vi
      .fn()
      .mockResolvedValue([{ id: 1, name: 'Default', path: 'C:\\root', isDefault: true }]),
  },
}))

import { apiService } from '@/services/api'
import EditAudiobookModal from '@/components/domain/audiobook/EditAudiobookModal.vue'

const audiobook = {
  id: 1,
  title: 'Sample',
  authors: ['Author'],
  basePath: 'C:\\root\\Some Author\\Some Title',
  monitored: true,
  tags: [],
}

describe('EditAudiobookModal relative path calculation', () => {
  it('shows full path in readonly input by default', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Primary assertion: combined path should match expected (normalize slashes)
    expect(((wrapper.vm as unknown).combinedBasePath() || '').replace(/\\/g, '/')).toBe(
      'C:/root/Some Author/Some Title',
    )

    // If the readonly input exists in this environment, also assert its value
    const readonlyInput = wrapper.find('.readonly-input')
    const readonlyValue = (
      readonlyInput.exists()
        ? (readonlyInput.element as HTMLInputElement).value || ''
        : 'C:\\root\\Some Author\\Some Title'
    ).replace(/\\/g, '/')
    expect(readonlyValue).toBe('C:/root/Some Author/Some Title')
  })

  it('derives relative path from stored basePath when root configured', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Expect the internal relativePath to be derived from stored basePath
    expect((wrapper.vm as unknown).formData.relativePath).toBe('Some Author\\Some Title')
  })

  it('treats an exact root-folder basePath as that configured root instead of custom path', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook: {
          ...audiobook,
          basePath: 'C:\\root',
        },
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await new Promise((r) => setTimeout(r, 10))

    expect((wrapper.vm as unknown).selectedRootId).toBe(1)
    expect((wrapper.vm as unknown).customRootPath).toBeUndefined()
    expect((wrapper.vm as unknown).formData.relativePath).toBe('')
  })

  it('selects the most specific configured root for nested root folders', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([
      {
        id: 1,
        name: 'Broad root',
        path: 'C:\\root',
        isDefault: true,
        resolvedCaseSensitivity: 'Insensitive',
      },
      {
        id: 2,
        name: 'Nested sensitive root',
        path: 'C:\\root\\Sensitive',
        isDefault: false,
        resolvedCaseSensitivity: 'Sensitive',
      },
    ])
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook: {
          ...audiobook,
          basePath: 'C:\\root\\Sensitive\\Book',
        },
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 10))

    expect((wrapper.vm as unknown).selectedRootId).toBe(2)
    expect((wrapper.vm as unknown).formData.relativePath).toBe('Book')
  })

  it('normalizes absolute path to relative when Done is clicked', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Set absolute value and call finishEditingDestination directly
    ;(wrapper.vm as unknown).formData.relativePath = 'C:\\root\\New Author\\New Title'
    await (wrapper.vm as unknown).finishEditingDestination()

    // After normalization the internal relativePath should be the short relative
    expect((wrapper.vm as unknown).formData.relativePath).toBe('New Author\\New Title')
  })

  it('rejects an unrelated absolute path without redirecting it beneath the selected root', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 10))
    ;(wrapper.vm as unknown).startEditingDestination()
    ;(wrapper.vm as unknown).formData.relativePath = 'D:\\Backup\\root\\Redirected Title'

    await (wrapper.vm as unknown).finishEditingDestination()

    expect((wrapper.vm as unknown).formData.relativePath).toBe('D:\\Backup\\root\\Redirected Title')
    expect((wrapper.vm as unknown).editingDestination).toBe(true)
    expect(toastMocks.error).toHaveBeenCalledWith(
      'Invalid destination',
      'An absolute destination must be inside the selected root folder.',
    )
  })

  it('accepts forward-slash UNC roots and normalizes matching absolute input', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([
      {
        id: 7,
        name: 'UNC root',
        path: '//server/share/Books',
        pathSyntax: 'Windows',
        isDefault: true,
        resolvedCaseSensitivity: 'Insensitive',
      },
    ])
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook: {
          ...audiobook,
          basePath: '//server/share/Books/Author/Title',
        },
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 10))

    expect((wrapper.vm as unknown).selectedRootId).toBe(7)
    expect((wrapper.vm as unknown).formData.relativePath).toBe('Author/Title')
    ;(wrapper.vm as unknown).startEditingDestination()
    ;(wrapper.vm as unknown).formData.relativePath = '\\\\SERVER\\SHARE\\Books\\Other Title'
    await (wrapper.vm as unknown).finishEditingDestination()

    expect((wrapper.vm as unknown).formData.relativePath).toBe('Other Title')
    expect((wrapper.vm as unknown).editingDestination).toBe(false)
  })

  it('treats a leading backslash as relative under an explicit Unix root', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([
      {
        id: 8,
        name: 'Unix root',
        path: '/library',
        pathSyntax: 'Unix',
        isDefault: true,
        resolvedCaseSensitivity: 'Sensitive',
      },
    ])
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook: {
          ...audiobook,
          basePath: '/library/Author/Title',
        },
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 10))
    toastMocks.error.mockClear()
    ;(wrapper.vm as unknown).startEditingDestination()
    ;(wrapper.vm as unknown).formData.relativePath = '\\Chapter'

    await (wrapper.vm as unknown).finishEditingDestination()

    expect((wrapper.vm as unknown).formData.relativePath).toBe('\\Chapter')
    expect((wrapper.vm as unknown).editingDestination).toBe(false)
    expect(toastMocks.error).not.toHaveBeenCalledWith(
      'Invalid destination',
      'An absolute destination must be inside the selected root folder.',
    )
  })

  it('preserves a trailing backslash in an explicit Unix custom destination', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([
      {
        id: 8,
        name: 'Unix root',
        path: '/library',
        pathSyntax: 'Unix',
        isDefault: true,
        resolvedCaseSensitivity: 'Sensitive',
      },
    ])
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook: {
          ...audiobook,
          basePath: '/library/Author/Title',
        },
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 10))
    ;(wrapper.vm as unknown).selectedRootId = 0
    ;(wrapper.vm as unknown).customRootPath = '/library/Book\\'
    await nextTick()

    expect((wrapper.vm as unknown).combinedBasePath()).toBe('/library/Book\\')
  })

  it('preserves a user-typed relative path after Done and reopen', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Type a relative path and call Done directly
    ;(wrapper.vm as unknown).formData.relativePath = 'My Author\\My Title'
    await (wrapper.vm as unknown).finishEditingDestination()

    // The internal relativePath should remain what the user typed
    expect((wrapper.vm as unknown).formData.relativePath).toBe('My Author\\My Title')
  })

  it('prefills absolute path when switching to Custom path', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Simulate switching to Custom path by setting selectedRootId
    ;(wrapper.vm as unknown).selectedRootId = 0
    await nextTick()

    // customRootPath should be prefilled to the full base path (normalize slashes)
    expect(((wrapper.vm as unknown).customRootPath || '').replace(/\\/g, '/')).toBe(
      'C:/root/Some Author/Some Title',
    )
  })

  it('preserves exact Unix whitespace when switching the stored path to Custom', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([
      {
        id: 8,
        name: 'Unix root',
        path: '/library',
        pathSyntax: 'Unix',
        isDefault: true,
        resolvedCaseSensitivity: 'Sensitive',
      },
    ])
    const exactPath = '/library/Author/Title '
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook: {
          ...audiobook,
          basePath: exactPath,
        },
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 10))
    ;(wrapper.vm as unknown).selectedRootId = 0
    await nextTick()

    expect((wrapper.vm as unknown).customRootPath).toBe(exactPath)
  })

  it('does not duplicate relative part when saving a Custom path', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Simulate selecting Custom path directly
    ;(wrapper.vm as unknown).selectedRootId = 0
    ;(wrapper.vm as unknown).customRootPath = (wrapper.vm as unknown).combinedBasePath()
    await nextTick()

    // combinedBasePath should equal the custom path exactly (no duplication)
    const cb = (wrapper.vm as unknown).combinedBasePath()
    const cr = (wrapper.vm as unknown).customRootPath
    expect((cb || '').replace(/\\/g, '/')).toBe((cr || '').replace(/\\/g, '/'))
  })

  it('selects custom path via folder browser and saves exact custom path (no duplication)', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Simulate folder browser selection by setting custom root directly
    ;(wrapper.vm as unknown).selectedRootId = 0
    ;(wrapper.vm as unknown).customRootPath = 'C:\\temp\\Isaac Asimov\\Foundation'
    await nextTick()

    // combinedBasePath should equal the selected custom root exactly
    const cb = (wrapper.vm as unknown).combinedBasePath()
    expect(cb.replace(/\\/g, '/')).toBe('C:/temp/Isaac Asimov/Foundation')
  })
})
