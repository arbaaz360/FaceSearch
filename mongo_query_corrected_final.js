// CORRECTED MongoDB query - checks if posts collection documents actually have posts
// A document in the 'posts' collection doesn't mean it has posts - we need to check post_list

db.followings.aggregate([
  {
    $lookup: {
      from: "posts",
      localField: "following_username",
      foreignField: "following_username",
      as: "postsDocs"
    }
  },
  {
    $addFields: {
      // Check if followings collection has posts in nested structure
      hasNestedPostsInFollowings: {
        $cond: {
          if: {
            $and: [
              { $ne: ["$response_data.data.post_list", null] },
              { $eq: [{ $type: "$response_data.data.post_list" }, "array"] },
              { $gt: [{ $size: "$response_data.data.post_list" }, 0] }
            ]
          },
          then: true,
          else: false
        }
      },
      // Check if posts collection documents actually have posts
      hasPostsInPostsCollection: {
        $cond: {
          if: { $eq: [{ $size: "$postsDocs" }, 0] },
          then: false,
          else: {
            $anyElementTrue: {
              $map: {
                input: "$postsDocs",
                as: "postDoc",
                in: {
                  $cond: {
                    if: {
                      $and: [
                        { $ne: ["$$postDoc.response_data.data.post_list", null] },
                        { $eq: [{ $type: "$$postDoc.response_data.data.post_list" }, "array"] },
                        { $gt: [{ $size: "$$postDoc.response_data.data.post_list" }, 0] }
                      ]
                    },
                    then: true,
                    else: false
                  }
                }
              }
            }
          }
        }
      }
    }
  },
  {
    $match: {
      $and: [
        { hasNestedPostsInFollowings: false },
        { hasPostsInPostsCollection: false }
      ]
    }
  },
  {
    $project: {
      _id: 0,
      username: "$following_username"
    }
  }
])

// SIMPLER VERSION - Check both collections properly
db.followings.aggregate([
  {
    $lookup: {
      from: "posts",
      localField: "following_username",
      foreignField: "following_username",
      as: "postsDocs"
    }
  },
  {
    $addFields: {
      // Count actual posts in followings collection
      postsInFollowings: {
        $cond: {
          if: {
            $and: [
              { $ne: ["$response_data.data.post_list", null] },
              { $eq: [{ $type: "$response_data.data.post_list" }, "array"] }
            ]
          },
          then: { $size: "$response_data.data.post_list" },
          else: 0
        }
      },
      // Count actual posts in posts collection documents
      postsInPostsCollection: {
        $reduce: {
          input: "$postsDocs",
          initialValue: 0,
          in: {
            $add: [
              "$$value",
              {
                $cond: {
                  if: {
                    $and: [
                      { $ne: ["$$this.response_data.data.post_list", null] },
                      { $eq: [{ $type: "$$this.response_data.data.post_list" }, "array"] }
                    ]
                  },
                  then: { $size: "$$this.response_data.data.post_list" },
                  else: 0
                }
              }
            ]
          }
        }
      }
    }
  },
  {
    $match: {
      $and: [
        { postsInFollowings: 0 },
        { postsInPostsCollection: 0 }
      ]
    }
  },
  {
    $project: {
      _id: 0,
      username: "$following_username",
      postsInFollowings: 1,
      postsInPostsCollection: 1
    }
  }
])

