import { describe, expect, it } from 'vitest'
import { getApiValidationError } from '@/services/apiErrors'

describe('getApiValidationError', () => {
  it('returns a matching structured field error and preserves the resolved destination', () => {
    const error = Object.assign(new Error('API error'), {
      status: 400,
      body: JSON.stringify({
        code: 'destination_path_outside_roots',
        field: 'destinationPath',
        message: 'DestinationPath must be inside a configured root folder or output path',
        resolvedDestination: '/outside/Author/Title',
      }),
    })

    expect(getApiValidationError(error, 'destinationPath')).toEqual({
      code: 'destination_path_outside_roots',
      field: 'destinationPath',
      message: 'DestinationPath must be inside a configured root folder or output path',
      resolvedDestination: '/outside/Author/Title',
    })
  })

  it('does not return an error for another field', () => {
    const error = Object.assign(new Error('API error'), {
      body: JSON.stringify({
        field: 'title',
        message: 'Title is invalid',
      }),
    })

    expect(getApiValidationError(error, 'destinationPath')).toBeNull()
  })

  it.each(['not-json', '{}', '{"message":""}'])(
    'fails closed for an unusable response body: %s',
    (body) => {
      expect(getApiValidationError(Object.assign(new Error('API error'), { body }))).toBeNull()
    },
  )
})
