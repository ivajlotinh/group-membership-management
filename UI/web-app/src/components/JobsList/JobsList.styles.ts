// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

import { IJobsListStyleProps, IJobsListStyles } from './JobsList.types';

export const getStyles = (props: IJobsListStyleProps): IJobsListStyles => {
  const { className } = props;

  return {
    root: [
      {
        
      },
      className
    ]
  };
};
