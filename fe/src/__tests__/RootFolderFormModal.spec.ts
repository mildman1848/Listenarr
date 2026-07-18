import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import RootFolderFormModal from '@/components/settings/RootFolderFormModal.vue'
import { useRootFoldersStore } from '@/stores/rootFolders'

const success = vi.fn()
const error = vi.fn()

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success, error }),
}))

describe('RootFolderFormModal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('rejects a Windows drive-relative root before submission', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const create = vi.spyOn(store, 'create')
    const wrapper = mount(RootFolderFormModal, {
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('input[placeholder="Enter a name for this root folder"]').setValue('Library')
    await wrapper.get('#root-path').setValue('C:')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(create).not.toHaveBeenCalled()
    expect(error).toHaveBeenCalledWith(
      'Validation Error',
      expect.stringContaining('separator after the drive letter'),
    )
  })

  it('rejects a relative root before submission', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const create = vi.spyOn(store, 'create')
    const wrapper = mount(RootFolderFormModal, {
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('input[placeholder="Enter a name for this root folder"]').setValue('Library')
    await wrapper.get('#root-path').setValue('relative/library')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(create).not.toHaveBeenCalled()
    expect(error).toHaveBeenCalledWith(
      'Validation Error',
      expect.stringContaining('absolute directory path'),
    )
  })

  it('uses the edited unambiguous path syntax instead of stale root metadata', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(RootFolderFormModal, {
      props: {
        root: {
          id: 8,
          name: 'Migrated Library',
          path: '//server/share/Books',
          pathSyntax: 'Windows',
          isDefault: false,
        },
      },
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('#root-path').setValue('/srv/CON')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(error).not.toHaveBeenCalled()
  })

  it('updates metadata directly for an equivalent Windows path', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const update = vi.spyOn(store, 'update').mockResolvedValue({
      id: 12,
      name: 'Library',
      path: 'C:\\Library',
      pathSyntax: 'Windows',
      isDefault: false,
      resolvedCaseSensitivity: 'Insensitive',
    })
    const wrapper = mount(RootFolderFormModal, {
      props: {
        root: {
          id: 12,
          name: 'Library',
          path: 'C:\\Library',
          pathSyntax: 'Windows',
          isDefault: false,
          caseSensitivityMode: 'Auto',
          resolvedCaseSensitivity: 'Insensitive',
        },
      },
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('#root-path').setValue('c:/library/')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    await vi.waitFor(() => expect(update).toHaveBeenCalledTimes(1))
    expect(update).toHaveBeenCalledWith(12, expect.objectContaining({ path: 'c:/library/' }))
    expect(success).toHaveBeenCalledWith('Success', 'Root folder updated')
  })

  it.each(['Sensitive', 'Unknown'] as const)(
    'treats a case-only edit as a path change when sensitive mode resolves as %s',
    async (resolvedCaseSensitivity) => {
      const pinia = createPinia()
      setActivePinia(pinia)
      const store = useRootFoldersStore()
      const update = vi.spyOn(store, 'update')
      const wrapper = mount(RootFolderFormModal, {
        props: {
          root: {
            id: 13,
            name: 'Library',
            path: 'C:\\Library',
            pathSyntax: 'Windows',
            isDefault: false,
            caseSensitivityMode: 'Sensitive',
            resolvedCaseSensitivity,
          },
        },
        global: {
          plugins: [pinia],
          stubs: {
            FolderBrowserModal: true,
          },
        },
      })
      await wrapper.get('#root-path').setValue('C:\\library')

      await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

      expect(update).not.toHaveBeenCalled()
      expect((wrapper.vm as unknown as { showConfirm: boolean }).showConfirm).toBe(true)
    },
  )

  it.each([
    [true, 'Root relocation started'],
    [false, 'Root path metadata updated'],
  ])('reports the path change accurately when moveFiles is %s', async (moveFiles, message) => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    vi.spyOn(store, 'update').mockResolvedValue({
      id: 7,
      name: 'Library',
      path: '/new-library',
      isDefault: true,
    })
    const wrapper = mount(RootFolderFormModal, {
      props: {
        root: {
          id: 7,
          name: 'Library',
          path: '/old-library',
          isDefault: true,
        },
      },
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('#root-path').setValue('/new-library')
    await (
      wrapper.vm as unknown as { confirmChange: (moveFiles: boolean) => Promise<void> }
    ).confirmChange(moveFiles)
    await vi.waitFor(() => expect(success).toHaveBeenCalledWith('Success', message))
  })
})
