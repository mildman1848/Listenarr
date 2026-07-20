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

// Mock apiService methods used during mount/seedPreview to avoid network calls
vi.mock('@/services/api', () => ({
  apiService: {
    getAudibleMetadata: vi.fn().mockResolvedValue({}),
    previewLibraryPath: vi
      .fn()
      .mockResolvedValue({ fullPath: 'C:\\root\\Author\\Title', relativePath: '' }),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: 'C:\\root' }),
    getQualityProfiles: vi.fn().mockResolvedValue([]),
    getRootFolders: vi.fn().mockResolvedValue([]),
    addToLibrary: vi.fn().mockResolvedValue({ audiobook: { id: 1 } }),
  },
}))

import AddLibraryModal from '@/components/domain/audiobook/AddLibraryModal.vue'

const fakeBook = {
  title: 'Test Title',
  authors: ['Author One'],
  imageUrl: '',
  asin: 'B001234567',
}

describe('AddLibraryModal relative path derivation', () => {
  it('shows and submits the same normalized effective destination', async () => {
    const { apiService } = await import('@/services/api')
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    const input = wrapper.get('input.relative-input')
    await input.setValue('Author/Title')
    await wrapper.vm.$nextTick()

    const preview = wrapper.get('[data-testid="effective-destination"]').text()
    expect(preview).toContain('C:\\root\\Author\\Title')

    await (wrapper.vm as unknown as { addToLibrary: () => Promise<void> }).addToLibrary()

    expect(apiService.addToLibrary).toHaveBeenCalledWith(
      expect.any(Object),
      expect.objectContaining({ destinationPath: 'C:\\root\\Author\\Title' }),
    )
  })

  it('shows relative path (full minus root) when preview returns fullPath and root configured', async () => {
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    // allow watchers / async ops
    await new Promise((r) => setTimeout(r, 10))

    const input = wrapper.find('input.relative-input')
    expect(input.exists()).toBe(true)
    expect((input.element as HTMLInputElement).value).toBe('Author\\Title')
  })
})
